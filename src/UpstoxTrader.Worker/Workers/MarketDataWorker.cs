using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UpstoxTrader.Core.Interfaces;
using UpstoxTrader.Core.Models;
using UpstoxTrader.Core.Settings;
using UpstoxTrader.Infrastructure.Auth;

namespace UpstoxTrader.Worker.Workers;

public class MarketDataWorker : BackgroundService
{
    private readonly IMarketFeedService _feed;
    private readonly ORBState _state;
    private readonly Channel<TickData> _niftyChannel;
    private readonly Channel<TickData> _breakoutChannel;
    private readonly Channel<TickData> _optionChannel;
    private readonly TokenManager _tokenManager;
    private readonly NiftySettings _nifty;
    private readonly ILogger<MarketDataWorker> _logger;

    public MarketDataWorker(
        IMarketFeedService feed,
        ORBState state,
        [FromKeyedServices("nifty-channel")] Channel<TickData> niftyChannel,
        [FromKeyedServices("breakout-channel")] Channel<TickData> breakoutChannel,
        [FromKeyedServices("option-channel")] Channel<TickData> optionChannel,
        TokenManager tokenManager,
        IOptions<NiftySettings> nifty,
        ILogger<MarketDataWorker> logger)
    {
        _feed = feed;
        _state = state;
        _niftyChannel = niftyChannel;
        _breakoutChannel = breakoutChannel;
        _optionChannel = optionChannel;
        _tokenManager = tokenManager;
        _nifty = nifty.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MarketDataWorker starting — waiting for token...");

        while (!_tokenManager.HasToken && !stoppingToken.IsCancellationRequested)
            await Task.Delay(2000, stoppingToken);

        if (stoppingToken.IsCancellationRequested) return;

        _logger.LogInformation("Token available — connecting to market feed");

        try
        {
            await _feed.ConnectAsync([_nifty.InstrumentKey], stoppingToken);

            await foreach (var tick in _feed.GetTickStreamAsync(stoppingToken))
            {
                try
                {
                    ProcessTick(tick);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing tick for {Key}", tick.InstrumentKey);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MarketDataWorker fatal error");
        }
    }

    private void ProcessTick(TickData tick)
    {
        if (tick.InstrumentKey == _nifty.InstrumentKey)
        {
            _state.PreviousNiftyLtp = _state.LastNiftyLtp;
            _state.LastNiftyLtp = tick.Ltp;

            _niftyChannel.Writer.TryWrite(tick);
            _breakoutChannel.Writer.TryWrite(tick);
        }

        var pos = _state.ActivePosition;
        if (pos is not null && tick.InstrumentKey == pos.InstrumentKey)
        {
            _optionChannel.Writer.TryWrite(tick);
        }
    }

    public async Task SubscribeToOptionAsync(string instrumentKey, CancellationToken ct)
    {
        _logger.LogInformation("Subscribing to option feed: {Key}", instrumentKey);
        await _feed.SubscribeAsync([instrumentKey], ct);
    }
}
