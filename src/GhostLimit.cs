#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// GhostLimit — spec: mnq-trading-bot/specs/orden-armada.md
// A "ghost" order that stays OFF the book until price touches your trigger line;
// only then is the real Limit/Stop entry submitted with an ATM template attached
// (native NT8 brackets, no hand-rolled OCO).

namespace NinjaTrader.NinjaScript.Indicators
{
	public enum GhostLimitEntryType { Stop = 0, Limit = 1 }

	public enum GhostLimitState { Disarmed = 0, Armed = 1, Triggered = 2, PositionOpen = 3, Error = 4 }

	// Reglas puras del indicador: sin estado, sin NinjaTrader, sin efectos secundarios.
	// Viven fuera de la clase Indicator a propósito, para poder probarlas con el mercado
	// cerrado y sin NT8 abierto — ver scripts/ninjascript/tests/GhostLimitTests.cs.
	public static class GhostLimitRules
	{
		// Separación por defecto entre gatillo y entrada al armar, en PUNTOS del instrumento
		// (MNQ: 1 punto = 4 ticks). 50 puntos dan margen para reacomodar las líneas sin
		// que el precio alcance el gatillo mientras el trader las arrastra.
		public const double DefaultSeparationPoints = 50.0;

		// Coloca las dos líneas repartidas a partes iguales alrededor del precio actual,
		// separadas exactamente separationPoints. En LONG el gatillo va abajo y la entrada
		// arriba; en SHORT al revés. Devuelve precios crudos: quien llama los redondea al tick.
		public static void DefaultLines(double refPrice, bool isLong, double separationPoints, out double trigger, out double entry)
		{
			double half = separationPoints / 2.0;
			if (isLong)
			{
				trigger = refPrice - half;
				entry = refPrice + half;
			}
			else
			{
				trigger = refPrice + half;
				entry = refPrice - half;
			}
		}

		// Spec R6: true cuando este trade alcanza o cruza el gatillo desde cualquier lado,
		// de modo que un gap que salta la línea limpia también dispara.
		public static bool IsTouched(double prevPrice, double price, double trigger)
		{
			return (price >= trigger && prevPrice <= trigger) || (price <= trigger && prevPrice >= trigger);
		}

		// Spec R8 (fail-closed): null = la entrada es segura de enviar; si no, el motivo
		// del rechazo. Bloquea el stop del lado equivocado y el limit que se llenaría al
		// instante. Precio exactamente igual a la entrada NO se bloquea en Limit: es la
		// semántica de retest con las líneas empalmadas.
		public static string ValidateEntry(bool isLong, GhostLimitEntryType entryType, double touchPrice, double entry)
		{
			if (entryType == GhostLimitEntryType.Stop)
			{
				if (isLong && !(touchPrice < entry))
					return string.Format("Buy stop invalid: price ({0}) is not below entry ({1}). Nothing was sent.", touchPrice, entry);
				if (!isLong && !(touchPrice > entry))
					return string.Format("Sell stop invalid: price ({0}) is not above entry ({1}). Nothing was sent.", touchPrice, entry);
			}
			else
			{
				if (isLong && touchPrice < entry)
					return string.Format("Buy limit would fill instantly: price ({0}) is below entry ({1}). Nothing was sent.", touchPrice, entry);
				if (!isLong && touchPrice > entry)
					return string.Format("Sell limit would fill instantly: price ({0}) is above entry ({1}). Nothing was sent.", touchPrice, entry);
			}
			return null;
		}
	}

	public class GhostLimit : Indicator
	{
		private const string Version = "1.1";
		private const string TagTrigger = "GL_TRIGGER";
		private const string TagEntry = "GL_ENTRY";

		// Spec R15 — solo una instancia puede tener una orden viva a la vez.
		// Se toma la posesión al ARMAR, no al cargar: NT8 crea la instancia nueva ANTES de
		// terminar la vieja al recargar la serie (p. ej. al cambiar el timeframe del chart),
		// así que un contador de instancias dejaba el indicador en ERROR permanente. Además
		// la posesión se recupera sola si quien la tiene ya no está armado ni disparado.
		private static GhostLimit armOwner;

		private GhostLimitState state = GhostLimitState.Disarmed;
		private bool isLong;
		private string errorReason = string.Empty;
		private bool mouseHooked;

		private Account accountObj;
		private Order entryOrder;
		private double sentPrice;
		private GhostLimitEntryType sentType;
		private DateTime triggeredAt;

		private double lastPrice;
		private double prevPrice;

		private SharpDX.RectangleF rectLong;
		private SharpDX.RectangleF rectShort;
		private SharpDX.RectangleF rectCancel;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name										= "GhostLimit";
				Description									= "Ghost order that stays off the book until price touches your trigger line; then your Limit/Stop entry is submitted with an ATM template (native NT8 brackets).";
				Calculate									= Calculate.OnEachTick;
				IsOverlay									= true;
				DisplayInDataBox							= false;
				PaintPriceMarkers							= false;
				IsAutoScale									= false;
				DrawOnPricePanel							= true;
				IsSuspendedWhileInactive					= false;
				BarsRequiredToPlot							= 0;

				AccountName									= "SimBot";
				AtmTemplate									= "60...40";
				EntryType									= GhostLimitEntryType.Stop;
				Quantity									= 2;
				PlaySoundOnTrigger							= true;
			}
			else if (State == State.DataLoaded)
			{
				RemoveDrawObject(TagTrigger);
				RemoveDrawObject(TagEntry);
				Print("[GhostLimit] v" + Version + " loaded on " + Instrument.FullName);

				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(() =>
					{
						ChartControl.PreviewMouseLeftButtonDown += OnChartClick;
						mouseHooked = true;
					});
			}
			else if (State == State.Terminated)
			{
				ReleaseArmOwnership();
				if (mouseHooked && ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(() =>
					{
						ChartControl.PreviewMouseLeftButtonDown -= OnChartClick;
						mouseHooked = false;
					});
			}
		}

		protected override void OnBarUpdate() { }

		#region Mouse / buttons
		private void OnChartClick(object sender, MouseButtonEventArgs e)
		{
			try
			{
				if (ChartControl == null)
					return;

				Point p = e.GetPosition(ChartControl);
				double scaleX = 1.0, scaleY = 1.0;
				PresentationSource source = PresentationSource.FromVisual(ChartControl);
				if (source != null && source.CompositionTarget != null)
				{
					scaleX = source.CompositionTarget.TransformToDevice.M11;
					scaleY = source.CompositionTarget.TransformToDevice.M22;
				}
				float x = (float)(p.X * scaleX);
				float y = (float)(p.Y * scaleY);

				if (rectLong.Contains(x, y))
				{
					e.Handled = true;
					if (CanArm())
						TriggerCustomEvent(o => Arm(true), null);
				}
				else if (rectShort.Contains(x, y))
				{
					e.Handled = true;
					if (CanArm())
						TriggerCustomEvent(o => Arm(false), null);
				}
				else if (rectCancel.Contains(x, y))
				{
					e.Handled = true;
					if (state == GhostLimitState.Armed || state == GhostLimitState.Triggered)
						TriggerCustomEvent(o => CancelPressed(), null);
				}
			}
			catch (Exception ex)
			{
				Print("[GhostLimit] Click handler exception: " + ex.Message);
			}
		}

		private bool CanArm()
		{
			return state == GhostLimitState.Disarmed || state == GhostLimitState.Error || state == GhostLimitState.PositionOpen;
		}

		// true si OTRA instancia tiene ahora mismo una orden viva (armada o disparada).
		// Una instancia vieja que quedó en Disarmed no bloquea: la posesión se recicla.
		private bool OtherInstanceIsLive()
		{
			GhostLimit other = armOwner;
			return other != null
				&& !ReferenceEquals(other, this)
				&& (other.state == GhostLimitState.Armed || other.state == GhostLimitState.Triggered);
		}

		private void ReleaseArmOwnership()
		{
			Interlocked.CompareExchange(ref armOwner, null, this);
		}
		#endregion

		#region Arm / Cancel
		private void Arm(bool goLong)
		{
			if (!CanArm())
				return;

			if (OtherInstanceIsLive())
			{
				SetError("Another GhostLimit instance already has a live order. Cancel it first.");
				return;
			}

			Account a = Account.All.FirstOrDefault(c => c.Name == AccountName);
			if (a == null)
			{
				SetError("Account '" + AccountName + "' was not found in NT8.");
				return;
			}

			string templatePath = Path.Combine(Core.Globals.UserDataDir, "templates", "AtmStrategy", AtmTemplate + ".xml");
			if (!File.Exists(templatePath))
			{
				SetError("ATM template '" + AtmTemplate + "' was not found.");
				return;
			}

			double refPrice = lastPrice > 0 ? lastPrice : (CurrentBar >= 0 ? Close[0] : 0);
			if (refPrice <= 0)
			{
				SetError("No reference price available to place the lines.");
				return;
			}

			double trigger, entry;
			GhostLimitRules.DefaultLines(refPrice, goLong, GhostLimitRules.DefaultSeparationPoints, out trigger, out entry);
			trigger = RoundTick(trigger);
			entry = RoundTick(entry);

			HorizontalLine lt = Draw.HorizontalLine(this, TagTrigger, trigger, Brushes.DarkOrange, DashStyleHelper.Dash, 2);
			lt.IsLocked = false;
			HorizontalLine le = Draw.HorizontalLine(this, TagEntry, entry, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
			le.IsLocked = false;

			accountObj = a;
			isLong = goLong;
			entryOrder = null;
			prevPrice = RoundTick(refPrice);
			errorReason = string.Empty;
			state = GhostLimitState.Armed;
			armOwner = this;

			Print(string.Format("[GhostLimit] {0} ARMED {1} | account={2} | ATM={3} | type={4} | trigger={5} | entry={6}",
				DateTime.Now.ToString("HH:mm:ss"), goLong ? "LONG" : "SHORT", AccountName, AtmTemplate, EntryType, trigger, entry));
			ForceRefresh();
		}

		private void CancelPressed()
		{
			if (state == GhostLimitState.Armed)
			{
				RemoveLines();
				state = GhostLimitState.Disarmed;
				Print("[GhostLimit] " + DateTime.Now.ToString("HH:mm:ss") + " cancelled while ARMED (no orders were in the market).");
			}
			else if (state == GhostLimitState.Triggered && entryOrder != null)
			{
				if (entryOrder.OrderState == OrderState.Initialized)
				{
					entryOrder = null;
					RemoveLines();
					state = GhostLimitState.Disarmed;
					Print("[GhostLimit] " + DateTime.Now.ToString("HH:mm:ss") + " discarded an entry that was never submitted (nothing was at the broker).");
				}
				else
				{
					try
					{
						accountObj.Cancel(new[] { entryOrder });
						Print("[GhostLimit] " + DateTime.Now.ToString("HH:mm:ss") + " cancel requested for the entry order (that order only).");
					}
					catch (Exception ex)
					{
						SetError("Failed to cancel the order: " + ex.Message);
					}
				}
			}
			ForceRefresh();
		}

		private void RemoveLines()
		{
			RemoveDrawObject(TagTrigger);
			RemoveDrawObject(TagEntry);
		}

		private void SetError(string reason)
		{
			state = GhostLimitState.Error;
			errorReason = reason;
			Print("[GhostLimit] " + DateTime.Now.ToString("HH:mm:ss") + " ERROR: " + reason);
			if (PlaySoundOnTrigger)
				PlaySound(Path.Combine(Core.Globals.InstallDir, "sounds", "Alert4.wav"));
			ForceRefresh();
		}

		private double RoundTick(double price)
		{
			return Instrument.MasterInstrument.RoundToTickSize(price);
		}
		#endregion

		#region Market watch
		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (e.MarketDataType != MarketDataType.Last)
				return;

			double p = RoundTick(e.Price);
			lastPrice = e.Price;

			if (State != State.Realtime)
			{
				prevPrice = p;
				return;
			}

			if (state == GhostLimitState.Armed)
				WatchTouch(p);
			else if (state == GhostLimitState.Triggered)
				WatchOrder();

			prevPrice = p;
		}

		private void WatchTouch(double p)
		{
			HorizontalLine lt = DrawObjects[TagTrigger] as HorizontalLine;
			HorizontalLine le = DrawObjects[TagEntry] as HorizontalLine;
			if (lt == null || le == null)
			{
				RemoveLines();
				state = GhostLimitState.Disarmed;
				Print("[GhostLimit] a line was removed from the chart; disarmed.");
				ForceRefresh();
				return;
			}

			double t = RoundTick(lt.StartAnchor.Price);
			if (!GhostLimitRules.IsTouched(prevPrice, p, t))
				return;

			FireEntry(p, RoundTick(le.StartAnchor.Price));
		}

		private void FireEntry(double touchPrice, double entry)
		{
			if (state != GhostLimitState.Armed)
				return;

			string reject = GhostLimitRules.ValidateEntry(isLong, EntryType, touchPrice, entry);
			if (reject != null)
			{
				RemoveLines();
				SetError(reject);
				return;
			}

			try
			{
				OrderAction action = isLong ? OrderAction.Buy : OrderAction.SellShort;
				OrderType orderType = EntryType == GhostLimitEntryType.Stop ? OrderType.StopMarket : OrderType.Limit;
				double lim = orderType == OrderType.Limit ? entry : 0;
				double stp = orderType == OrderType.StopMarket ? entry : 0;

				// NT8 requirement: the entry order's name MUST be "Entry",
				// otherwise StartAtmStrategy silently never submits the order.
				entryOrder = accountObj.CreateOrder(Instrument, action, orderType, OrderEntry.Manual, TimeInForce.Day,
					Quantity, lim, stp, string.Empty, "Entry", Core.Globals.MaxDate, null);
				AtmStrategy atm = AtmStrategy.StartAtmStrategy(AtmTemplate, entryOrder);
				if (atm == null)
				{
					entryOrder = null;
					RemoveLines();
					SetError("ATM strategy failed to start (template '" + AtmTemplate + "'). Nothing was sent.");
					return;
				}
			}
			catch (Exception ex)
			{
				entryOrder = null;
				RemoveLines();
				SetError("Order submit failed: " + ex.Message);
				return;
			}

			triggeredAt = DateTime.Now;

			sentPrice = entry;
			sentType = EntryType;
			state = GhostLimitState.Triggered;
			RemoveDrawObject(TagTrigger);

			Print(string.Format("[GhostLimit] {0} TRIGGERED: touch={1} -> {2} {3} {4} {5} @ {6} | account={7} | ATM={8}",
				DateTime.Now.ToString("HH:mm:ss"), touchPrice, sentType, isLong ? "BUY" : "SELLSHORT",
				Quantity, Instrument.MasterInstrument.Name, entry, AccountName, AtmTemplate));
			if (PlaySoundOnTrigger)
				PlaySound(Path.Combine(Core.Globals.InstallDir, "sounds", "Alert2.wav"));
			ForceRefresh();
		}

		private void WatchOrder()
		{
			if (entryOrder == null)
			{
				state = GhostLimitState.Disarmed;
				return;
			}

			if (entryOrder.OrderState == OrderState.Initialized)
			{
				if ((DateTime.Now - triggeredAt).TotalSeconds > 5)
				{
					entryOrder = null;
					RemoveLines();
					SetError("Entry was never submitted (ATM did not start). Nothing is working at the broker.");
				}
				return;
			}

			if (entryOrder.OrderState == OrderState.Filled)
			{
				state = GhostLimitState.PositionOpen;
				Print("[GhostLimit] " + DateTime.Now.ToString("HH:mm:ss") + " entry FILLED @ " + entryOrder.AverageFillPrice + "; ATM is managing the position.");
				ForceRefresh();
			}
			else if (entryOrder.OrderState == OrderState.Cancelled)
			{
				Print("[GhostLimit] " + DateTime.Now.ToString("HH:mm:ss") + " entry order cancelled.");
				entryOrder = null;
				RemoveLines();
				state = GhostLimitState.Disarmed;
				ForceRefresh();
			}
			else if (entryOrder.OrderState == OrderState.Rejected)
			{
				entryOrder = null;
				RemoveLines();
				SetError("Entry order was REJECTED by the broker/sim (see NT8 Log).");
			}
		}
		#endregion

		#region Render
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (IsInHitTest || RenderTarget == null || ChartPanel == null)
				return;

			float btnH = 26f;
			float gap = 8f;
			float topMargin = 40f;
			float rightEdge = ChartPanel.X + ChartPanel.W - 12f;
			float y0 = ChartPanel.Y + topMargin;

			rectCancel = new SharpDX.RectangleF(rightEdge - 72f, y0, 72f, btnH);
			rectShort = new SharpDX.RectangleF(rectCancel.X - gap - 88f, y0, 88f, btnH);
			rectLong = new SharpDX.RectangleF(rectShort.X - gap - 80f, y0, 80f, btnH);

			bool armEnabled = CanArm();
			bool cancelEnabled = state == GhostLimitState.Armed || state == GhostLimitState.Triggered;

			using (SharpDX.DirectWrite.TextFormat tf = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", SharpDX.DirectWrite.FontWeight.SemiBold, SharpDX.DirectWrite.FontStyle.Normal, 13f))
			using (SharpDX.DirectWrite.TextFormat tfStatus = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, 12f))
			{
				tf.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
				tf.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
				tfStatus.TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing;

				DrawButton(rectLong, "Arm Long", new SharpDX.Color(46, 125, 50, 255), armEnabled, tf);
				DrawButton(rectShort, "Arm Short", new SharpDX.Color(198, 40, 40, 255), armEnabled, tf);
				DrawButton(rectCancel, "Cancel", new SharpDX.Color(97, 97, 97, 255), cancelEnabled, tf);

				string text;
				SharpDX.Color statusColor;
				switch (state)
				{
					case GhostLimitState.Armed:
						text = string.Format("ARMED {0} | {1} | {2} | {3}\nDrag the lines: orange = trigger, blue = entry.",
							isLong ? "LONG" : "SHORT", AccountName, AtmTemplate, EntryType);
						statusColor = new SharpDX.Color(255, 167, 38, 255);
						break;
					case GhostLimitState.Triggered:
						text = string.Format("TRIGGERED | {0} {1} {2} @ {3} (working, {4})\nMoving the blue line does NOT requote the order.",
							sentType, isLong ? "BUY" : "SELL", Quantity, sentPrice, AccountName);
						statusColor = new SharpDX.Color(66, 165, 245, 255);
						break;
					case GhostLimitState.PositionOpen:
						text = "POSITION OPEN | managed by ATM/Chart Trader. Arm again for a new order.";
						statusColor = new SharpDX.Color(102, 187, 106, 255);
						break;
					case GhostLimitState.Error:
						text = "ERROR: " + errorReason;
						statusColor = new SharpDX.Color(239, 83, 80, 255);
						break;
					default:
						text = "GhostLimit v" + Version + " | DISARMED | use Arm Long / Arm Short.";
						statusColor = new SharpDX.Color(158, 158, 158, 255);
						break;
				}

				float leftEdge = ChartPanel.X + 10f;
				SharpDX.RectangleF textRect = new SharpDX.RectangleF(leftEdge, y0 + btnH + 6f, Math.Max(320f, rightEdge - leftEdge), 40f);
				using (SharpDX.Direct2D1.SolidColorBrush statusBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, statusColor))
					RenderTarget.DrawText(text, tfStatus, textRect, statusBrush);
			}
		}

		private void DrawButton(SharpDX.RectangleF rect, string label, SharpDX.Color borderColor, bool enabled, SharpDX.DirectWrite.TextFormat tf)
		{
			byte alpha = enabled ? (byte)230 : (byte)80;
			using (SharpDX.Direct2D1.SolidColorBrush fill = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)33, (byte)33, (byte)33, alpha)))
			using (SharpDX.Direct2D1.SolidColorBrush border = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(borderColor.R, borderColor.G, borderColor.B, alpha)))
			using (SharpDX.Direct2D1.SolidColorBrush textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)255, (byte)255, (byte)255, alpha)))
			{
				RenderTarget.FillRectangle(rect, fill);
				RenderTarget.DrawRectangle(rect, border, 1.5f);
				RenderTarget.DrawText(label, tf, rect, textBrush);
			}
		}
		#endregion

		#region Parameters
		[Display(Name = "Account", GroupName = "Parameters", Order = 1, Description = "NT8 account the order is sent to.")]
		public string AccountName { get; set; }

		[Display(Name = "ATM template", GroupName = "Parameters", Order = 2, Description = "Exact ATM template name (without .xml).")]
		public string AtmTemplate { get; set; }

		[Display(Name = "Entry type", GroupName = "Parameters", Order = 3, Description = "Stop = enter on the comeback (confirmation). Limit = enter on the retest of the line.")]
		public GhostLimitEntryType EntryType { get; set; }

		[Range(1, 20)]
		[Display(Name = "Quantity", GroupName = "Parameters", Order = 4, Description = "Entry contracts; must match the ATM template.")]
		public int Quantity { get; set; }

		[Display(Name = "Play sound on trigger", GroupName = "Parameters", Order = 5)]
		public bool PlaySoundOnTrigger { get; set; }
		#endregion
	}
}
