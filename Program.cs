using BotCripto.Data;
using BotCripto.Services;
using BotCripto.Strategies;
using Newtonsoft.Json.Linq;

bool quietBacktest = false;

// Log su file dell'output solo per i backtest (Logs/backtest_yyyy-MM-dd.log); con --quiet (ottimizzazione) niente file.
// In esecuzione normale il bot scrive solo a schermo: nessun file cycles_yyyy-MM-dd.log.
if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault()?.StartsWith("--backtest", StringComparison.OrdinalIgnoreCase) == true
    && !Environment.GetCommandLineArgs().Contains("--quiet"))
    FileLogger.Install(Path.Combine(AppContext.BaseDirectory, "Logs"), "backtest");
Console.WriteLine($"▶️  Avvio Bot 14 - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC - eseguibile in {AppContext.BaseDirectory}");

// Qualsiasi errore che chiude il processo viene scritto a schermo, così si sa perché il bot si è fermato
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.WriteLine($"💥 ERRORE FATALE, il bot si chiude: {e.ExceptionObject}");
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.WriteLine($"⚠️  Eccezione non gestita in un task: {e.Exception}");
    e.SetObserved();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    Console.WriteLine($"⏹️  Bot 14 terminato - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
Console.CancelKeyPress += (_, _) =>
    Console.WriteLine("⏹️  Chiusura richiesta con Ctrl+C");

Console.WriteLine(@"
╔════════════════════════════════════════════════════════╗
║       🤖 BOT 14 - ERTF - AZIONI USA (Yahoo Finance)    ║
║                 Backtest + Live Trading                ║
╚════════════════════════════════════════════════════════╝
");

// All'avvio apre la connessione a SQL Server (database "bot"); nel log scrive solo se si è connesso.
// Se il database non è raggiungibile il bot continua usando Data/trades.json.
BotDatabase.Initialize();

// Importo per trade, timeframe e R:R sono letti da appsettings.json (TradingSettings / MarketMonitoring).
// Nessun importo totale investibile: il capitale disponibile è illimitato.
var appSettings = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
var tradingSettings = appSettings["TradingSettings"] ?? throw new InvalidOperationException("appsettings.json: sezione TradingSettings mancante");
var marketSettings = appSettings["MarketMonitoring"] ?? throw new InvalidOperationException("appsettings.json: sezione MarketMonitoring mancante");
decimal CapitalPerTrade = tradingSettings.Value<decimal?>("CapitalPerTrade") ?? throw new InvalidOperationException("appsettings.json: CapitalPerTrade mancante");
decimal RewardRiskRatio =tradingSettings.Value<decimal?>("RewardRiskRatio") ?? throw new InvalidOperationException("appsettings.json: RewardRiskRatio mancante");
string Timeframe = marketSettings.Value<string>("TimeframeForCandlesticks") ?? throw new InvalidOperationException("appsettings.json: TimeframeForCandlesticks mancante");

// I dati Yahoo Finance (YFSharp) usati dal bot sono solo candele giornaliere
if (Timeframe != "1d")
{
    Console.WriteLine($"⚠️  Timeframe {Timeframe} non supportato per le azioni USA: uso candele giornaliere (1d)");
    Timeframe = "1d";
}

// Check se è backtest mode
var cmdArgs = Environment.GetCommandLineArgs();
bool isBacktestMode = cmdArgs.Length > 1 && cmdArgs[1].ToLower() == "--backtest";
bool isRealBacktestMode = cmdArgs.Length > 1 && cmdArgs[1].ToLower() == "--backtest-real";

if (isBacktestMode)
{
    await RunBacktestAsync();
}
else if (isRealBacktestMode)
{
    // --backtest-real [giorni | yyyy-MM-dd yyyy-MM-dd] [--symbols file] [--rr rapporto]   (solo candele giornaliere)
    // --rr: take profit come multiplo della distanza dello stop (default RewardRiskRatio)
    // --symbols: elenco di simboli da usare (uno per riga); se il file non esiste viene creato con
    //            l'elenco scaricato, così più backtest possono usare esattamente lo stesso paniere.
    var positional = new List<string>();
    var timeframe = Timeframe;
    string? symbolsFile = null;
    var rewardRisk = RewardRiskRatio;
    for (int a = 2; a < cmdArgs.Length; a++)
    {
        if (cmdArgs[a] == "--timeframe" && a + 1 < cmdArgs.Length)
        {
            if (cmdArgs[++a] != "1d")
                Console.WriteLine($"⚠️  --timeframe {cmdArgs[a]} ignorato: per le azioni USA sono disponibili solo candele giornaliere (1d)");
        }
        else if (cmdArgs[a] == "--symbols" && a + 1 < cmdArgs.Length)
            symbolsFile = cmdArgs[++a];
        else if (cmdArgs[a] == "--rr" && a + 1 < cmdArgs.Length)
            rewardRisk = decimal.Parse(cmdArgs[++a], System.Globalization.CultureInfo.InvariantCulture);
        else if (cmdArgs[a] == "--set" && a + 1 < cmdArgs.Length)
            StrategyOptions.Apply(cmdArgs[++a]);   // es. --set RsiPullback=50,TrailAtr=2
        else if (cmdArgs[a] == "--dump" && a + 1 < cmdArgs.Length)
            StrategyOptions.DumpPath = cmdArgs[++a];
        else if (cmdArgs[a] == "--quiet")
            quietBacktest = true;
        else
            positional.Add(cmdArgs[a]);
    }

    if (StrategyOptions.RewardRisk > 0)
        rewardRisk = StrategyOptions.RewardRisk;

    var endDate = DateTime.UtcNow;
    var startDate = endDate.AddDays(-30);
    var dateStyle = System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal;
    if (positional.Count >= 2
        && DateTime.TryParseExact(positional[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, dateStyle, out var fromDate)
        && DateTime.TryParseExact(positional[1], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, dateStyle, out var toDate))
    {
        startDate = fromDate;
        endDate = toDate.AddDays(1).AddTicks(-1); // data finale inclusa
    }
    else if (positional.Count >= 1 && int.TryParse(positional[0], out var parsedDays))
    {
        startDate = endDate.AddDays(-parsedDays);
    }
    await RunRealBacktestAsync(startDate, endDate, timeframe, symbolsFile, rewardRisk);
}
else
{
    await RunLiveAsync();
}

async Task RunLiveAsync()
{
    var scheduler = new BotSchedulerService(Timeframe, RewardRiskRatio, CapitalPerTrade);

    Console.WriteLine("Strategia Principale Attiva:");
    Console.WriteLine(StrategyOptions.Direction == 0 ? "  ⭐ Swing Trading Short (solo posizioni short)" : "  ⭐ Swing Trading Long (solo posizioni long)");
    Console.WriteLine($"     • Configurazione: EMA 20/50, RSI 14, ATR 14 su candele {Timeframe}");
    Console.WriteLine(StrategyOptions.Direction == 0
        ? "     • Setup: trend ribassista, rimbalzo sull'EMA20, ripartenza al ribasso; short solo con SPY sotto EMA50"
        : "     • Setup: trend rialzista, ritracciamento sull'EMA20, ripartenza al rialzo; long solo con SPY sopra EMA50; stop sotto il minimo a 20 giorni, stop minimo 8%");
    Console.WriteLine("     • Modalità: paper trading (segnali e trade simulati, nessun ordine reale)");
    Console.WriteLine("\nImpostazioni:");
    Console.WriteLine("  • Dati: Yahoo Finance (YFSharp), azioni USA più grandi per capitalizzazione (candele giornaliere)");
    Console.WriteLine("  • Capitale investibile: illimitato");
    Console.WriteLine("  • Ricerca segnali: una volta per seduta, a mercato chiuso");
    Console.WriteLine($"  • Importo per trade: ${CapitalPerTrade:F2}");
    Console.WriteLine($"  • Take profit: {RewardRiskRatio}x la distanza dello stop");
    Console.WriteLine("  • Report Settimanale: Lunedì 00:00");
    Console.WriteLine("  • Tracking Metriche: Ogni 10 cicli");

    try
    {
        await scheduler.StartAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Errore: {ex.Message}");
    }
    finally
    {
        scheduler.Stop();
    }
}

async Task RunRealBacktestAsync(DateTime startDate, DateTime endDate, string timeframe, string? symbolsFile, decimal rewardRisk)
{
    Console.WriteLine($"\n🔍 MODALITA' BACKTEST SU DATI STORICI REALI ATTIVATA ({startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}, {timeframe}, capitale illimitato, ${CapitalPerTrade:F2} per trade, R:R {rewardRisk})\n");

    var backtestService = new BacktestService(rewardRisk, CapitalPerTrade);

    try
    {
        // Senza elenco (o con un file che non esiste ancora) si usano le azioni USA più grandi per capitalizzazione a fine periodo
        List<string>? symbols = null;
        bool saveSymbols = symbolsFile != null && !File.Exists(symbolsFile);
        if (symbolsFile != null && !saveSymbols)
        {
            symbols = File.ReadAllLines(symbolsFile).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
            Console.WriteLine($"📄 Simboli letti da {symbolsFile}: {symbols.Count}");
        }

        var result = await backtestService.RunBacktestAsync(symbols, startDate, endDate, timeframe);
        if (saveSymbols)
        {
            File.WriteAllLines(symbolsFile!, result.Symbols);
            Console.WriteLine($"📄 Elenco simboli salvato in {symbolsFile}");
        }
        if (quietBacktest)
        {
            // Riga di riepilogo per l'ottimizzazione: P&L netto di commissioni e tasse (26% sul saldo netto positivo)
            decimal Net(IEnumerable<BotCripto.Models.Trade> ts) { var g = ts.Sum(t => t.Profit ?? 0); return g > 0 ? g * (1 - 0.26m) : g; }
            var closed = result.Trades.Where(t => t.Status == "Closed").ToList();
            var split = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var h1 = closed.Where(t => t.CloseTime < split).ToList();
            var h2 = closed.Where(t => t.CloseTime >= split).ToList();
            var wins = closed.Where(t => t.Profit > 0).Sum(t => t.Profit ?? 0);
            var losses = Math.Abs(closed.Where(t => t.Profit <= 0).Sum(t => t.Profit ?? 0));
            Console.WriteLine($"SUMMARY|{StrategyOptions.Describe()}|trades={closed.Count}|net={Net(closed):F2}|gross={closed.Sum(t => t.Profit ?? 0):F2}|pf={(losses > 0 ? wins / losses : 0):F2}|win={(closed.Count > 0 ? (decimal)closed.Count(t => t.Profit > 0) / closed.Count * 100 : 0):F1}|h1net={Net(h1):F2}|h1n={h1.Count}|h2net={Net(h2):F2}|h2n={h2.Count}|dd={result.MaxDrawdownAmount:F2}");
        }
        else
            backtestService.PrintBacktestReport(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Errore nel backtest: {ex.Message}\n{ex.StackTrace}");
    }
}

async Task RunBacktestAsync()
{
    Console.WriteLine("\n🔍 MODALITA' BACKTEST SINTETICO ATTIVATA\n");

    var backtest = new SyntheticBacktest(initialCapital: 100m);

    try
    {
        // Esegui backtest su 365 giorni (1 anno)
        // con 20 simboli, 55% win-rate atteso, 8 trade per simbolo
        var result = backtest.RunBacktest(
            numSymbols: 20,
            daysOfData: 365,
            expectedWinRate: 0.55m,
            tradesPerSymbol: 8
        );

        // Stampa report dettagliato
        backtest.PrintBacktestReport(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Errore nel backtest: {ex.Message}\n{ex.StackTrace}");
    }
}

