using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using mitelapi;
using mitelapi.Messages;
using mitelapi.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace eventphone.ommstats
{
    class Program:IDisposable
    {
        private readonly OmmClient _client;
        private RFPStatNameType[] _statNames;
        private IRfpSummary _rfpSummary;
        private IPPDevSummary _ppSummary;
        private IPPUserSummary _userSummary;
        private readonly string _graphiteHost;
        private readonly int _graphitePort;
        private readonly string _username;
        private readonly string _password;
        private readonly long _interval;
        private static ILoggerFactory _loggerFactory;
        private readonly ILogger<OmmClient> _ommLogger;
        private readonly ILogger _logger;

        static void Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", optional: true)
                .Build();

            var serviceCollection = new ServiceCollection()
                .AddLogging(o => o.AddConfiguration(configuration.GetSection("Logging"))
                    .AddConsole()
                    .AddDebug());
            var provider = serviceCollection.BuildServiceProvider();
            _loggerFactory = provider.GetRequiredService<ILoggerFactory>();

            var ommHost = configuration.GetValue<string>("Hostname");
            var ommPort = configuration.GetValue<int>("Port");
            var username = configuration.GetValue<string>("Username");
            var password = configuration.GetValue<string>("Password");
            var graphiteHost = configuration.GetValue<string>("GraphiteHost");
            var graphitePort = configuration.GetValue<int>("GraphitePort");
            var interval = configuration.GetValue<long>("Interval");

            using (var instance = new Program(ommHost, ommPort, username, password, graphiteHost, graphitePort, interval))
            {
                using (var cts = new CancellationTokenSource())
                {
                    var logger = _loggerFactory.CreateLogger("OmmStats");
                    Console.CancelKeyPress += Cancelled;
                    try
                    {
                        instance.RunAsync(cts.Token).GetAwaiter().GetResult();
                    }
                    catch (TaskCanceledException)
                    {
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error while executing OmmStats");
                        throw;
                    }
                    Console.CancelKeyPress -= Cancelled;
                    void Cancelled(object s, ConsoleCancelEventArgs e)
                    {
                        logger.LogInformation("stopping...");
                        e.Cancel = true;
                        cts.Cancel();
                    }
                }
            }
        }

        private Program(string ommHost, int ommPort, string username, string password, string graphiteHost, int graphitePort, long interval)
        {
            _client = new OmmClient(ommHost, ommPort);
            _username = username;
            _password = password;
            _graphiteHost = graphiteHost;
            _graphitePort = graphitePort;
            _interval = interval;

            _logger = _loggerFactory.CreateLogger("OmmStats");
            _ommLogger = _loggerFactory.CreateLogger<OmmClient>();
            _client.MessageLog += Log;
        }

        private void Log(object sender, LogMessageEventArgs e)
        {
            var prefix = e.Direction == MessageDirection.In ? "<OMM: " : ">OMM:";
            _ommLogger.LogDebug(prefix + e.Message);
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            await _client.LoginAsync(_username, _password, cancellationToken: cancellationToken);
            var statNamesTask = _client.GetRFPStatisticConfigAsync(cancellationToken);
            await _client.SubscribeAsync(new[]
            {
                new SubscribeCmdType(EventType.RFPSummary), 
                new SubscribeCmdType(EventType.PPDevSummary),
                new SubscribeCmdType(EventType.PPUserSummary),
            }, cancellationToken);
            _client.RfpSummary += (s, e) => { _rfpSummary = e.Event; };
            _client.PPDevSummary += (s, e) => { _ppSummary = e.Event; };
            _client.PPUserSummary += (s, e) => { _userSummary = e.Event; };
            _rfpSummary = await _client.GetRFPSummaryAsync(cancellationToken);
            _ppSummary = await _client.GetPPDevSummaryAsync(cancellationToken);
            _userSummary = await _client.GetPPUserSummaryAsync(cancellationToken);
            _statNames = (await statNamesTask).Name;
            while (!cancellationToken.IsCancellationRequested)
            {
                await SendMetricsAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(_interval), cancellationToken);
            }
        }
        
        private async Task SendMetricsAsync(CancellationToken cancellationToken)
        {
            var values = new Dictionary<string, double>
            {
                {"omm.rfp.total", _rfpSummary.TotalCount},
                {"omm.rfp.connected", _rfpSummary.ConnectedCount},
                {"omm.rfp.active", _rfpSummary.DectActiveCount},
                {"omm.rfp.activated", _rfpSummary.DectActivatedCount},
                {"omm.pp.total", _ppSummary.TotalCount},
                {"omm.pp.subscribed", _ppSummary.SubscribedCount},
                {"omm.user.total", _userSummary.TotalCount},
                {"omm.user.locatable", _userSummary.LocatableCount},
                {"omm.user.sipregistrations", _userSummary.SipRegistrationCount},
                {"omm.mgr.rtt", _client.Rtt.TotalSeconds},
            };
            await AddRfpStatsAsync(values, cancellationToken);
            await SendMetricValuesAsync(values, cancellationToken);
            _logger.LogDebug("Sent metrics");
        }

        private async Task SendMetricValuesAsync(IDictionary<string, double> values, CancellationToken cancellationToken)
        {
            using (var client = new TcpClient(AddressFamily.InterNetworkV6))
            {
                client.Client.DualMode = true;
                await client.ConnectAsync(_graphiteHost, _graphitePort);
                cancellationToken.ThrowIfCancellationRequested();
                using (var stream = client.GetStream())
                {
                    using (var writer = new StreamWriter(stream) {NewLine = "\n"})
                    {
                        var timestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
                        foreach (var datapoint in values)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var line = $"{datapoint.Key} {datapoint.Value.ToString(CultureInfo.InvariantCulture)} {timestamp}";
                            _logger.LogDebug(">graphite: " + line);
                            await writer.WriteLineAsync(line);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        await writer.FlushAsync();
                    }
                }
            }
        }

        private async Task AddRfpStatsAsync(Dictionary<string, double> target, CancellationToken cancellationToken)
        {
            var id = 0;
            const int count = 20;
            GetRFPStatisticResp rfpStats;
            do
            {
                rfpStats = await _client.GetRFPStatisticAsync(id, count, 0, cancellationToken);
                var escaper = new GraphiteEscaper();
                foreach (var statName in _statNames)
                {
                    var group = escaper.Escape(statName.Group);
                    var metric = escaper.Escape(statName.Name);
                    if (rfpStats.Data == null) continue;
                    foreach (var rfp in rfpStats.Data)
                    {
                        var value = rfp.Values[statName.Id];
                        target.Add($"omm.rfpstats.{group}.{metric}.{rfp.Id}", value);
                    }
                }
                if (rfpStats.Data != null)
                  id = rfpStats.Data.Max(x => x.Id) + 1;
            } while (rfpStats.Data != null && rfpStats.Data.Length == count);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _client.Dispose();
                _loggerFactory.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
