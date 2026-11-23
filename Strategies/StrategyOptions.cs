using System.Globalization;
using System.Reflection;

namespace BotCripto.Strategies;

// Parametri della strategia Swing Trading Short. I valori di default sono quelli usati dal bot;
// in backtest si possono sovrascrivere da riga di comando con --set Nome=valore,Nome2=valore2
// (serve per l'ottimizzazione dei parametri).
public static class StrategyOptions
{
    public static int FastEma = 20;
    public static int SlowEma = 50;
    public static int MarketRegimeEmaPeriod = 50;
    public static int PullbackLookback = 8;          // candele entro cui deve essere avvenuto il rimbalzo
    public static int SwingLookback = 20;          // candele per il massimo dello swing (stop)
    public static int AtrPeriod = 14;
    public static decimal AtrStopBuffer = 0.5m;      // buffer sopra lo swing, in ATR
    public static decimal RsiPullback = 45m;         // l'RSI deve essere salito sopra questa soglia durante il rimbalzo
    public static decimal RsiMin = 30m;              // niente short con RSI già ipervenduto
    public static decimal MaxStopPercent = 0.15m;    // stop più largo del 15% = setup scartato
    public static decimal MinStopPercent = 0.08m;     // stop più stretto di questa quota = setup scartato (le commissioni lo mangerebbero)
    public static decimal PullbackMaxAboveSlow = 1.05m; // il rimbalzo non deve superare EMA50 * questo fattore
    public static int LongEmaPeriod = 0;             // se > 0: Close deve stare sotto questa EMA (es. 200)
    public static decimal MinSlowSlopePct = 0m;      // pendenza minima EMA50 negativa su 10 candele: (EMA50 - EMA50[-10]) / EMA50 <= -valore
    public static decimal MarketMinBelowPct = 0m;    // SPY deve stare almeno questa quota sotto la propria EMA (0 = basta sotto)
    public static bool MarketFastBelowSlow = false;  // SPY: EMA20 < EMA50 oltre a Close < EMA
    public static decimal MinReversalBodyAtr = 0m;   // corpo della candela di ripartenza almeno X ATR
    public static decimal MinDollarVolume = 0m;      // volume medio 20 gg * prezzo minimo
    public static decimal MinPrice = 0m;
    public static int Direction = 1;                 // 0 = solo short; 1 = solo long (default); 2 = long con SPY rialzista, short con SPY ribassista
    public static int Mode = 0;                     // 0 = rimbalzo sull'EMA20 (default); 1 = breakdown dei minimi
    public static int BreakoutLookback = 20;         // modalità 1: minimo degli ultimi N giorni da rompere
    public static decimal BreakoutVolRatio = 1.5m;   // modalità 1: volume almeno X volte la media a 20 giorni
    public static decimal BreakoutRsiMax = 100m;     // modalità 1: RSI massimo ammesso
    public static decimal BreakStopAtr = 1.5m;       // modalità 1: stop a X ATR sopra la chiusura
    public static bool IgnoreRegime = false;         // solo test: ignora il filtro SPY (resta il trend del titolo)
    public static decimal MarketMaxDrop10 = 0m;     // 0 = off; altrimenti niente short se SPY ha perso più di X negli ultimi 10 giorni
    public static decimal MinRet10 = -1m;            // rendimento minimo del titolo a 10 giorni (-1 = nessun filtro)
    public static decimal MinDistLow20 = -1m;        // distanza minima dal minimo dei 20 giorni precedenti (-1 = nessun filtro)

    // Gestione dell'uscita / selezione dei segnali (usate dal backtest e dallo scheduler)
    public static int MaxHoldDays = 0;               // 0 = nessun limite; altrimenti chiusura a mercato dopo N sedute
    public static int MaxNewPerDay = 0;              // 0 = nessun limite; altrimenti solo i N segnali più forti del giorno
    public static decimal TrailAtr = 0m;             // 0 = off; altrimenti stop che segue il minimo a X ATR (solo backtest)
    public static decimal RewardRisk = 0m;           // 0 = usa RewardRiskRatio di appsettings / --rr

    public static string? DumpPath = null;           // se impostato: CSV dei trade con le variabili d'ingresso (solo analisi)

    public static void Apply(string assignments)
    {
        foreach (var part in assignments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            var field = typeof(StrategyOptions).GetField(kv[0], BindingFlags.Public | BindingFlags.Static)
                ?? throw new ArgumentException($"Parametro sconosciuto: {kv[0]}");
            if (field.FieldType == typeof(int))
                field.SetValue(null, int.Parse(kv[1], CultureInfo.InvariantCulture));
            else if (field.FieldType == typeof(decimal))
                field.SetValue(null, decimal.Parse(kv[1], CultureInfo.InvariantCulture));
            else if (field.FieldType == typeof(bool))
                field.SetValue(null, bool.Parse(kv[1]));
        }
    }

    public static string Describe() =>
        string.Join(",", typeof(StrategyOptions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => $"{f.Name}={Convert.ToString(f.GetValue(null), CultureInfo.InvariantCulture)}"));
}
