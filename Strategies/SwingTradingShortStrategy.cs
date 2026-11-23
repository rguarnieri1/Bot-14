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
    private static int FastEma => StrategyOptions.FastEma;
    private static int SlowEma => StrategyOptions.SlowEma;
    private static int MarketRegimeEmaPeriod => StrategyOptions.MarketRegimeEmaPeriod;
    private static int PullbackLookback => StrategyOptions.PullbackLookback;
    private static int SwingLookback => StrategyOptions.SwingLookback;
    private static int AtrPeriod => StrategyOptions.AtrPeriod;
    private static decimal AtrStopBuffer => StrategyOptions.AtrStopBuffer;
    private static decimal RsiPullback => StrategyOptions.RsiPullback;
    private static decimal RsiMin => StrategyOptions.RsiMin;
    private static decimal MaxStopPercent => StrategyOptions.MaxStopPercent;
    public static int MinCandles => Math.Max(SlowEma + 20, StrategyOptions.LongEmaPeriod > 0 ? StrategyOptions.LongEmaPeriod + 5 : 0);

    // Regime di mercato: true se l'indice (SPY) chiude sopra la sua EMA50 sull'ultima candela, false se sotto,
    // null se i dati non bastano.
    public static bool? IsMarketBullish(List<Candle> marketCandles)
    {
        var ema = TechnicalIndicators.CalculateEMA(marketCandles.Select(c => c.Close).ToList(), MarketRegimeEmaPeriod);
        if (ema.Count == 0)
            return null;
        var close = marketCandles.Last().Close;
        if (close > ema.Last())
            return true;
        // Filtri opzionali più severi: SPY deve stare nettamente sotto la EMA e/o con EMA veloce sotto la lenta
        if (StrategyOptions.MarketMinBelowPct > 0 && close > ema.Last() * (1 - StrategyOptions.MarketMinBelowPct))
            return true;
        // Dopo un crollo di SPY negli ultimi 10 giorni il rimbalzo è più probabile: niente nuovi short
        if (StrategyOptions.MarketMaxDrop10 > 0 && marketCandles.Count > 10
            && close / marketCandles[marketCandles.Count - 11].Close - 1 < -StrategyOptions.MarketMaxDrop10)
            return true;
        if (StrategyOptions.MarketFastBelowSlow)
        {
            var closes = marketCandles.Select(c => c.Close).ToList();
            var f = TechnicalIndicators.CalculateEMA(closes, StrategyOptions.FastEma);
            var s = TechnicalIndicators.CalculateEMA(closes, StrategyOptions.SlowEma);
            if (f.Count == 0 || s.Count == 0 || f.Last() >= s.Last())
                return true;
        }
        return false;
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

        // Direzione: 0 = solo short (default), 1 = solo long, 2 = long con SPY rialzista e short con SPY ribassista
        if (StrategyOptions.Direction == 1 || (StrategyOptions.Direction == 2 && marketBullish == true))
            return AnalyzeLong(result, candles, marketBullish);

        // 0) Regime di mercato (controllo economico, fatto prima del calcolo degli indicatori)
        if (marketBullish != false && !(StrategyOptions.IgnoreRegime && marketBullish == true))
        {
            result.Rejection = RejectionReason.AgainstMarketRegime;
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

        // 1) Trend di fondo ribassista
        if (!(last.Close < e50 && e20 < e50))
        {
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }
        if (StrategyOptions.LongEmaPeriod > 0)
        {
            var longEma = TechnicalIndicators.CalculateEMA(closes, StrategyOptions.LongEmaPeriod);
            if (longEma.Count == 0 || last.Close >= longEma.Last())
            {
                result.Rejection = RejectionReason.TrendNotAligned;
                return result;
            }
        }
        if (StrategyOptions.MinSlowSlopePct > 0 && ema50.Count > 10 && (e50 - E50(10)) / e50 > -StrategyOptions.MinSlowSlopePct)
        {
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }
        if (last.Close < StrategyOptions.MinPrice)
        {
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }
        if (StrategyOptions.MinDollarVolume > 0 && candles.Count >= 20
            && candles.TakeLast(20).Average(c => c.Volume) * last.Close < StrategyOptions.MinDollarVolume)
        {
            result.Rejection = RejectionReason.VolumeInsufficient;
            return result;
        }

        // Modalità 1: breakdown dei minimi (rottura al ribasso con volume), invece del rimbalzo sull'EMA20
        if (StrategyOptions.Mode == 1)
        {
            int n = StrategyOptions.BreakoutLookback;
            if (candles.Count < n + 2)
            {
                result.Rejection = RejectionReason.InsufficientData;
                return result;
            }
            var priorLow = candles.Skip(candles.Count - 1 - n).Take(n).Min(c => c.Low);
            var avgVolume = candles.Skip(candles.Count - 21).Take(20).Average(c => c.Volume);
            var atrB = CalculateAtr(candles, AtrPeriod);
            var rsiB = Rsi(0);
            if (!(last.Close < priorLow) || !(last.Close < last.Open)
                || avgVolume <= 0 || last.Volume < avgVolume * StrategyOptions.BreakoutVolRatio
                || rsiB < RsiMin || rsiB > StrategyOptions.BreakoutRsiMax)
            {
                result.Rejection = RejectionReason.NoReversal;
                return result;
            }
            var stopB = last.Close + atrB * StrategyOptions.BreakStopAtr;
            var stopPctB = (stopB - last.Close) / last.Close;
            if (stopPctB > MaxStopPercent || stopPctB < StrategyOptions.MinStopPercent)
            {
                result.Rejection = RejectionReason.StopLossError;
                return result;
            }
            result.IsSignal = true;
            result.Signal = "🔴 SELL (short) - Breakdown dei minimi a " + n + " giorni";
            result.Indicators["StopLoss"] = stopB;
            result.Indicators["ATR"] = atrB;
            result.Indicators["SignalStrength"] = (priorLow - last.Close) / atrB + (avgVolume > 0 ? last.Volume / avgVolume : 0);
            if (candles.Count > 21)
            {
                result.Indicators["F_StopPct"] = stopPctB;
                result.Indicators["F_AtrPct"] = atrB / last.Close;
                result.Indicators["F_Rsi"] = rsiB;
                result.Indicators["F_Gap"] = (e50 - e20) / e50;
                result.Indicators["F_Ret10"] = last.Close / candles[candles.Count - 11].Close - 1;
                result.Indicators["F_Ret20"] = last.Close / candles[candles.Count - 21].Close - 1;
                result.Indicators["F_DistLow20"] = last.Close / priorLow - 1;
                result.Indicators["F_VolRatio"] = last.Volume / avgVolume;
                result.Indicators["F_BodyAtr"] = atrB > 0 ? (last.Open - last.Close) / atrB : 0;
                result.Indicators["F_DollarVol"] = avgVolume * last.Close;
                result.Indicators["F_Price"] = last.Close;
            }
            return result;
        }

        // 2) Rimbalzo recente
        bool pullback = false;
        for (int back = 1; back <= PullbackLookback; back++)
        {
            var c = candles[candles.Count - 1 - back];
            if (c.High >= E20(back) && c.High <= E50(back) * StrategyOptions.PullbackMaxAboveSlow && Rsi(back) > RsiPullback)
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
        if (stop <= last.Close || (stop - last.Close) / last.Close > MaxStopPercent
            || (stop - last.Close) / last.Close < StrategyOptions.MinStopPercent)
        {
            result.Rejection = RejectionReason.StopLossError;
            return result;
        }
        if (StrategyOptions.MinReversalBodyAtr > 0 && (last.Open - last.Close) < atr * StrategyOptions.MinReversalBodyAtr)
        {
            result.Rejection = RejectionReason.NoReversal;
            return result;
        }

        if (candles.Count > 21)
        {
            var ret10 = last.Close / candles[candles.Count - 11].Close - 1;
            var distLow20 = last.Close / candles.Skip(candles.Count - 21).Take(20).Min(c => c.Low) - 1;
            if (ret10 < StrategyOptions.MinRet10 || distLow20 < StrategyOptions.MinDistLow20)
            {
                result.Rejection = RejectionReason.TrendNotAligned;
                return result;
            }
        }

        result.IsSignal = true;
        result.Signal = "🔴 SELL (short) - Swing Trading Short (pullback EMA20 + ripartenza ribassista)";
        result.Indicators["StopLoss"] = stop;
        result.Indicators["ATR"] = atr;
        // Variabili descrittive dell'ingresso (per l'analisi dei trade, non usate per decidere)
        if (candles.Count > 21)
        {
            result.Indicators["F_StopPct"] = (stop - last.Close) / last.Close;
            result.Indicators["F_AtrPct"] = atr / last.Close;
            result.Indicators["F_Rsi"] = rsiNow;
            result.Indicators["F_Gap"] = (e50 - e20) / e50;
            result.Indicators["F_Ret10"] = last.Close / candles[candles.Count - 11].Close - 1;
            result.Indicators["F_Ret20"] = last.Close / candles[candles.Count - 21].Close - 1;
            var prevLow20 = candles.Skip(candles.Count - 21).Take(20).Min(c => c.Low);
            result.Indicators["F_DistLow20"] = last.Close / prevLow20 - 1;
            var avgVol = candles.Skip(candles.Count - 21).Take(20).Average(c => c.Volume);
            result.Indicators["F_VolRatio"] = avgVol > 0 ? last.Volume / avgVol : 0;
            result.Indicators["F_BodyAtr"] = atr > 0 ? (last.Open - last.Close) / atr : 0;
            result.Indicators["F_DollarVol"] = avgVol * last.Close;
            result.Indicators["F_Price"] = last.Close;
        }
        // Più forte = trend più marcato (distanza EMA20/EMA50) e ripartenza più decisa (RSI basso)
        result.Indicators["SignalStrength"] = (e50 - e20) / e50 * 100m + (100m - rsiNow) / 10m;
        return result;
    }

    // Versione long (specchio della short): SPY sopra la EMA, titolo in trend rialzista,
    // ritracciamento sull'EMA20 e ripartenza (Mode 0) oppure rottura dei massimi con volume (Mode 1).
    private AnalysisResult AnalyzeLong(AnalysisResult result, List<Candle> candles, bool? marketBullish)
    {
        if (marketBullish != true && !StrategyOptions.IgnoreRegime)
        {
            result.Rejection = RejectionReason.AgainstMarketRegime;
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
        decimal E20(int back) => ema20[ema20.Count - 1 - back];
        decimal E50(int back) => ema50[ema50.Count - 1 - back];
        decimal Rsi(int back) => rsi[rsi.Count - 1 - back];

        var last = candles.Last();
        var prev = candles[candles.Count - 2];
        result.CurrentPrice = last.Close;
        var e20 = E20(0);
        var e50 = E50(0);
        var rsiNow = Rsi(0);

        if (!(last.Close > e50 && e20 > e50))
        {
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }
        if (StrategyOptions.LongEmaPeriod > 0)
        {
            var longEma = TechnicalIndicators.CalculateEMA(closes, StrategyOptions.LongEmaPeriod);
            if (longEma.Count == 0 || last.Close <= longEma.Last())
            {
                result.Rejection = RejectionReason.TrendNotAligned;
                return result;
            }
        }
        if (last.Close < StrategyOptions.MinPrice)
        {
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }
        var avgVolume = candles.Count >= 21 ? candles.Skip(candles.Count - 21).Take(20).Average(c => c.Volume) : 0m;
        if (StrategyOptions.MinDollarVolume > 0 && avgVolume * last.Close < StrategyOptions.MinDollarVolume)
        {
            result.Rejection = RejectionReason.VolumeInsufficient;
            return result;
        }

        var atr = CalculateAtr(candles, AtrPeriod);
        decimal stop;
        decimal strength;

        if (StrategyOptions.Mode == 1)
        {
            int n = StrategyOptions.BreakoutLookback;
            if (candles.Count < n + 2)
            {
                result.Rejection = RejectionReason.InsufficientData;
                return result;
            }
            var priorHigh = candles.Skip(candles.Count - 1 - n).Take(n).Max(c => c.High);
            if (!(last.Close > priorHigh) || !(last.Close > last.Open)
                || avgVolume <= 0 || last.Volume < avgVolume * StrategyOptions.BreakoutVolRatio
                || rsiNow > 100m - RsiMin)
            {
                result.Rejection = RejectionReason.NoReversal;
                return result;
            }
            stop = last.Close - atr * StrategyOptions.BreakStopAtr;
            strength = (last.Close - priorHigh) / atr + last.Volume / avgVolume;
        }
        else
        {
            bool pullback = false;
            for (int back = 1; back <= PullbackLookback; back++)
            {
                var c = candles[candles.Count - 1 - back];
                if (c.Low <= E20(back) && c.Low >= E50(back) * (2m - StrategyOptions.PullbackMaxAboveSlow) && Rsi(back) < 100m - RsiPullback)
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
            bool reversal = last.Close > last.Open && last.Close > e20 && last.Close > prev.High;
            if (!reversal || rsiNow <= Rsi(1))
            {
                result.Rejection = RejectionReason.NoReversal;
                return result;
            }
            if (rsiNow > 100m - RsiMin)
            {
                result.Rejection = RejectionReason.RsiExtreme;
                return result;
            }
            var swingLow = candles.Skip(candles.Count - SwingLookback).Min(c => c.Low);
            stop = swingLow - atr * AtrStopBuffer;
            strength = (e20 - e50) / e50 * 100m + rsiNow / 10m;
        }

        var stopPct = (last.Close - stop) / last.Close;
        if (stop >= last.Close || stopPct > MaxStopPercent || stopPct < StrategyOptions.MinStopPercent)
        {
            result.Rejection = RejectionReason.StopLossError;
            return result;
        }

        result.IsSignal = true;
        result.Signal = "🟢 BUY (long) - " + (StrategyOptions.Mode == 1 ? "Breakout dei massimi" : "Pullback EMA20 + ripartenza rialzista");
        result.Indicators["IsLong"] = 1;
        result.Indicators["StopLoss"] = stop;
        result.Indicators["ATR"] = atr;
        result.Indicators["SignalStrength"] = strength;
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

