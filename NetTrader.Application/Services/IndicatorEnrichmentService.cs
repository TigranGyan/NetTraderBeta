using Microsoft.Extensions.Logging;
using NetTrader.Domain.Entities;
using NetTrader.Domain.Interfaces;
using Skender.Stock.Indicators;

namespace NetTrader.Application.Services;

/// <summary>
/// FIX #10: Обогащение MarketData техническими индикаторами.
/// 
/// Ранее ~80 строк try/catch-блоков жили прямо в TradingBotWorker.ExecuteAsync,
/// делая его нетестируемым и раздутым до 150+ строк.
/// Теперь это отдельный сервис с единой ответственностью.
/// </summary>
public class IndicatorEnrichmentService
{
	private readonly IMarketDataProvider _market;
	private readonly ILogger<IndicatorEnrichmentService> _logger;

	public IndicatorEnrichmentService(IMarketDataProvider market, ILogger<IndicatorEnrichmentService> logger)
	{
		_market = market;
		_logger = logger;
	}

	/// <summary>
	/// Обогащает список кандидатов 4h и 1h индикаторами.
	/// FIX #19: принимает CancellationToken для graceful shutdown.
	/// </summary>
	public async Task EnrichAllAsync(List<MarketData> candidates, CancellationToken ct = default)
	{
		foreach (var c in candidates)
		{
			ct.ThrowIfCancellationRequested();

			try
			{
				await Enrich4hIndicatorsAsync(c);
				await Enrich1hIndicatorsAsync(c);
				await Task.Delay(50, ct);
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Ошибка обогащения {Symbol}", c.Symbol);
			}
		}
	}

	private async Task Enrich4hIndicatorsAsync(MarketData c)
	{
		var klines4h = await _market.GetKLinesAsync(c.Symbol, "4h", 300);
		if (klines4h.Count < 30) return;

		var quotes4h = klines4h.Select(k => new Quote
		{
			Date = k.OpenTime,
			Open = k.Open,
			High = k.High,
			Low = k.Low,
			Close = k.Close,
			Volume = k.Volume
		}).ToList();

		c.Rsi = GetLastValue(quotes4h.GetRsi(14), x => x.Rsi, 50m);
		c.Ema200 = GetLastValue(quotes4h.GetEma(200), x => x.Ema, c.CurrentPrice);
		c.Atr = GetLastValue(quotes4h.GetAtr(14), x => x.Atr, 0m);

		try
		{
			var macdList = quotes4h.GetMacd(12, 26, 9).ToList();
			var lastMacd = macdList.LastOrDefault(x => x.Macd.HasValue);
			if (lastMacd != null)
			{
				c.MacdHistogram = (decimal)(lastMacd.Histogram ?? 0);
				c.MacdLine = (decimal)(lastMacd.Macd ?? 0);
				c.MacdSignal = (decimal)(lastMacd.Signal ?? 0);
			}
		}
		catch { /* indicator calc failure — defaults remain */ }

		try
		{
			var bbList = quotes4h.GetBollingerBands(20, 2).ToList();
			var lastBb = bbList.LastOrDefault(x => x.UpperBand.HasValue);
			if (lastBb != null)
			{
				c.BbUpper = (decimal)(lastBb.UpperBand ?? (double)c.CurrentPrice);
				c.BbLower = (decimal)(lastBb.LowerBand ?? (double)c.CurrentPrice);
				c.BbMiddle = (decimal)(lastBb.Sma ?? (double)c.CurrentPrice);
			}
			else SetDefaultBb4h(c);
		}
		catch { SetDefaultBb4h(c); }

		c.Adx = GetLastValue(quotes4h.GetAdx(14), x => x.Adx, 25m);

		// Volume ratio
		var volumes = klines4h.Select(k => k.Volume).ToList();
		try
		{
			decimal avg = volumes.Count > 20 ? volumes.TakeLast(20).Average() : volumes.Average();
			c.VolumeRatio = avg > 0 ? volumes.Last() / avg : 1m;
		}
		catch { c.VolumeRatio = 1m; }

		// Annualized volatility
		var closes4h = klines4h.Select(k => k.Close).ToList();
		try
		{
			if (closes4h.Count > 1)
			{
				var ret = Enumerable.Range(1, closes4h.Count - 1)
					.Select(i => Math.Log((double)closes4h[i] / (double)closes4h[i - 1])).ToList();
				double var_ = ret.Sum(r => r * r) / ret.Count;
				c.Volatility24h = (decimal)(Math.Sqrt(var_) * Math.Sqrt(6.0 * 365.0) * 100);
			}
		}
		catch { c.Volatility24h = 2m; }
	}

	private async Task Enrich1hIndicatorsAsync(MarketData c)
	{
		try
		{
			var klines1h = await _market.GetKLinesAsync(c.Symbol, "1h", 100);
			if (klines1h.Count < 26) return;

			var quotes1h = klines1h.Select(k => new Quote
			{
				Date = k.OpenTime,
				Open = k.Open,
				High = k.High,
				Low = k.Low,
				Close = k.Close,
				Volume = k.Volume
			}).ToList();

			c.Rsi1h = GetLastValue(quotes1h.GetRsi(14), x => x.Rsi, 50m);

			try
			{
				var macd1h = quotes1h.GetMacd(12, 26, 9).ToList();
				var last = macd1h.LastOrDefault(x => x.Macd.HasValue);
				c.MacdHist1h = last != null ? (decimal)(last.Histogram ?? 0) : 0m;
			}
			catch { }

			try
			{
				var bb1h = quotes1h.GetBollingerBands(20, 2).ToList();
				var lastBb = bb1h.LastOrDefault(x => x.UpperBand.HasValue);
				if (lastBb != null)
				{
					c.BbUpper1h = (decimal)(lastBb.UpperBand ?? (double)c.CurrentPrice);
					c.BbLower1h = (decimal)(lastBb.LowerBand ?? (double)c.CurrentPrice);
				}
				else SetDefaultBb1h(c);
			}
			catch { SetDefaultBb1h(c); }
		}
		catch (Exception ex)
		{
			_logger.LogWarning("1h данные {Symbol}: {E}", c.Symbol, ex.Message);
		}
	}

	// ═══ Helpers ═══

	private static decimal GetLastValue<T>(IEnumerable<T> series, Func<T, double?> selector, decimal fallback)
	{
		try
		{
			var list = series.ToList();
			var last = list.LastOrDefault(x => selector(x).HasValue);
			return last != null ? (decimal)selector(last)!.Value : fallback;
		}
		catch { return fallback; }
	}

	private static void SetDefaultBb4h(MarketData c)
	{
		c.BbUpper = c.CurrentPrice * 1.02m;
		c.BbLower = c.CurrentPrice * 0.98m;
		c.BbMiddle = c.CurrentPrice;
	}

	private static void SetDefaultBb1h(MarketData c)
	{
		c.BbUpper1h = c.CurrentPrice * 1.01m;
		c.BbLower1h = c.CurrentPrice * 0.99m;
	}
}