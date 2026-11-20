using BotCripto.Models;

namespace BotCripto.Strategies;

// Swing trading short-only su azioni USA, candele giornaliere chiuse:
//  0) regime di mercato: SPY sotto la sua EMA50 (si va corti solo con mercato ribassista)
//  1) trend di fondo ribassista: Close < EMA50 e EMA20 < EMA50
//  2) rimbalzo: nelle ultime candele l'RSI14 è salito sopra RsiPullback e il massimo ha toccato l'EMA20 (restando sotto l'EMA50)
//  3) ripartenza al ribasso: candela ribassista che chiude sotto l'EMA20 e sotto il minimo precedente,
//     RSI in calo e non ipervenduto
// Stop loss sopra il massimo dello swing recente (più un buffer ATR); il target lo calcola il RiskManager (R:R configurato).
public class SwingTradingShortStrategy
{
    private const int FastEma = 20;
    private const int SlowEma = 50;
    private const int MarketRegimeEmaPeriod = 50;
    private const int PullbackLookback = 8;      // candele entro cui deve essere avvenuto il rimbalzo
    private const int SwingLookback = 10;        // candele per il massimo dello swing (stop)
    private const int AtrPeriod = 14;
    private const decimal AtrStopBuffer = 0.5m;  // buffer sopra lo swing, in ATR
    private const decimal RsiPullback = 45m;     // l'RSI deve essere salito sopra questa soglia durante il rimbalzo
    private const decimal RsiMin = 30m;          // niente short con RSI già ipervenduto
    private const decimal MaxStopPercent = 0.15m; // stop più largo del 15% = setup scartato
    public const int MinCandles = SlowEma + 20;

    // Regime di mercato: true se l'indice (SPY) chiude sopra la sua EMA50 sull'ultima candela, false se sotto,
    // null se i dati non bastano.
    public static bool? IsMarketBullish(List<Candle> marketCandles)
    {
        var ema = TechnicalIndicators.CalculateEMA(marketCandles.Select(c => c.Close).ToList(), MarketRegimeEmaPeriod);
        if (ema.Count == 0)
            return null;
        return marketCandles.Last().Close > ema.Last();
    }

    // marketBullish: regime di mercato (vedi IsMarketBullish). Short solo con mercato ribassista;
    // se il regime è sconosciuto (null) il segnale viene scartato.
    public AnalysisResult Analyze(string symbol, List<Candle> candles, bool? marketBullish)
    {
        var result = new AnalysisResult
        {
            Symbol = symbol,
            StrategyName = "Swing Trading Short",
            IsSignal = false,
            Signal = "No Signal",
            AnalysisTime = DateTime.UtcNow
        };

        if (candles.Count < MinCandles)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var closes = candles.Select(c => c.Close).ToList();
        var ema20 = TechnicalIndicators.CalculateEMA(closes, FastEma);
        var ema50 = TechnicalIndicators.CalculateEMA(closes, SlowEma);
        var rsi = TechnicalIndicators.CalculateRSI(closes);
        if (ema20.Count < PullbackLookback + 1 || ema50.Count < PullbackLookback + 1 || rsi.Count < PullbackLookback + 2)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var last = candles.Last();
        var prev = candles[candles.Count - 2];
        result.CurrentPrice = last.Close;

        // Le serie sono allineate alla fine: back = candele indietro dall'ultima
        decimal E20(int back) => ema20[ema20.Count - 1 - back];
        decimal E50(int back) => ema50[ema50.Count - 1 - back];
        decimal Rsi(int back) => rsi[rsi.Count - 1 - back];

        var e20 = E20(0);
        var e50 = E50(0);
        result.Indicators["EMA20"] = e20;
        result.Indicators["EMA50"] = e50;
        result.Indicators["Trend"] = -1;

        // 0) Regime di mercato
        if (marketBullish != false)
        {
            result.Rejection = RejectionReason.AgainstMarketRegime;
            return result;
        }

        // 1) Trend di fondo ribassista
        if (!(last.Close < e50 && e20 < e50))
        {
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }

        // 2) Rimbalzo recente
        bool pullback = false;
        for (int back = 1; back <= PullbackLookback; back++)
        {
            var c = candles[candles.Count - 1 - back];
            if (c.High >= E20(back) && c.High <= E50(back) * 1.05m && Rsi(back) > RsiPullback)
            {
                pullback = true;
                break;
            }
        }
        if (!pullback)
        {
            result.Rejection = RejectionReason.NoPullback;
            return result;
        }

        // 3) Ripartenza al ribasso
        var rsiNow = Rsi(0);
        result.Indicators["RSI"] = rsiNow;
        bool reversal = last.Close < last.Open && last.Close < e20 && last.Close < prev.Low;
        if (!reversal || rsiNow >= Rsi(1))
        {
            result.Rejection = RejectionReason.NoReversal;
            return result;
        }
        if (rsiNow < RsiMin)
        {
            result.Rejection = RejectionReason.RsiExtreme;
            return result;
        }

        // Stop sopra lo swing high recente, con buffer ATR
        var atr = CalculateAtr(candles, AtrPeriod);
        var swingHigh = candles.Skip(candles.Count - SwingLookback).Max(c => c.High);
        var stop = swingHigh + atr * AtrStopBuffer;
        if (stop <= last.Close || (stop - last.Close) / last.Close > MaxStopPercent)
        {
            result.Rejection = RejectionReason.StopLossError;
            return result;
        }

        result.IsSignal = true;
        result.Signal = "🔴 SELL (short) - Swing Trading Short (pullback EMA20 + ripartenza ribassista)";
        result.Indicators["StopLoss"] = stop;
        result.Indicators["ATR"] = atr;
        // Più forte = trend più marcato (distanza EMA20/EMA50) e ripartenza più decisa (RSI basso)
        result.Indicators["SignalStrength"] = (e50 - e20) / e50 * 100m + (100m - rsiNow) / 10m;
        return result;
    }

    private static decimal CalculateAtr(List<Candle> candles, int period)
    {
        var trs = new List<decimal>();
        for (int i = candles.Count - period; i < candles.Count; i++)
        {
            var c = candles[i];
            var prevClose = candles[i - 1].Close;
            trs.Add(Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose))));
        }
        return trs.Average();
    }
}
