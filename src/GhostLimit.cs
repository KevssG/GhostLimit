#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
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

		// v1.2.4 — gracia entre el fill y la primera lectura fiable de la posición. La
		// ejecución y la actualización de Account.Positions no son atómicas: justo después
		// del fill la cuenta puede leerse "plana" un instante sin que la posición haya
		// cerrado. Pasada la gracia, plana es plana (p. ej. entrada y stop entre dos ticks).
		public const double FlatGraceSeconds = 10.0;

		// v1.2.4 — única regla que decide si se borra la línea de entrada por cierre de la
		// posición. Solo aplica con POSITION OPEN: una orden ARMADA (aún sin ejecutar) o
		// TRIGGERED (trabajando en el broker) conserva sus líneas aunque la cuenta esté
		// plana, porque plana es justamente su estado normal. Un cierre por TP, por SL o
		// por Flatten manual se ve igual desde aquí: la cuenta vuelve a plana.
		//   sawPosition       la posición se leyó distinta de plana en algún momento tras el fill
		//   isFlatNow         lectura actual de la cuenta para el instrumento del chart
		//   secondsSinceFill  segundos desde que la entrada se reportó FILLED
		public static bool ShouldClearAfterClose(GhostLimitState state, bool sawPosition, bool isFlatNow, double secondsSinceFill, double graceSeconds)
		{
			if (state != GhostLimitState.PositionOpen)
				return false;
			if (!isFlatNow)
				return false;
			return sawPosition || secondsSinceFill >= graceSeconds;
		}
	}

	public class GhostLimit : Indicator
	{
		private const string Version = "1.2.4";
		// Safety cap inherited from the old Quantity parameter's Range(1,20).
		private const int MaxQuantity = 20;
		private const string TagTrigger = "GL_TRIGGER";
		private const string TagEntry = "GL_ENTRY";

		// Spec R15 — solo una instancia puede tener una orden viva a la vez.
		// Se toma la posesión al ARMAR, no al cargar: NT8 crea la instancia nueva ANTES de
		// terminar la vieja al recargar la serie (p. ej. al cambiar el timeframe del chart),
		// así que un contador de instancias dejaba el indicador en ERROR permanente. Además
		// la posesión se recupera sola si quien la tiene ya no está armado ni disparado.
		private static GhostLimit armOwner;

		// v1.2.2 — dueño del clic por chart. Al recargar la serie (F5 / Reload NinjaScript /
		// cambio de timeframe) NT8 crea la instancia nueva y termina la vieja DESPUÉS; si en
		// ese Terminated `ChartControl` ya es null, el handler de la vieja se queda enganchado
		// al chart. Como se suscribió antes, corre primero, pone e.Handled = true y su
		// TriggerCustomEvent ya no ejecuta nada (la instancia está terminada): el clic muere
		// ahí y la instancia viva —la que pinta el banner— nunca lo ve. Eso es el "botón mudo".
		// Regla: la ÚLTIMA instancia que se enganchó a un ChartControl es la dueña de sus
		// clics; cualquier otra deja pasar el evento sin tocarlo. Solo se lee/escribe en el
		// hilo de UI (hook, Terminated y handler corren ahí), por eso no lleva lock.
		private static readonly System.Collections.Generic.Dictionary<ChartControl, GhostLimit> clickOwner
			= new System.Collections.Generic.Dictionary<ChartControl, GhostLimit>();

		// Sello corto por instancia para distinguir en Output la viva de la fantasma.
		private readonly string instanceId = Guid.NewGuid().ToString("N").Substring(0, 4);

		// v1.2.3 — diagnóstico a FICHERO además de Output. La ventana Output no persiste y
		// no se puede leer desde fuera de NT8; el fichero sí. Todo en try/catch: el
		// diagnóstico nunca puede romper lo que diagnostica.
		private static readonly string DiagPath = Path.Combine(Core.Globals.UserDataDir, "GhostLimit_diag.log");

		private void Diag(string msg)
		{
			string line = "[GhostLimit#" + instanceId + "] " + DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg;
			try { Print(line); } catch { }
			try { File.AppendAllText(DiagPath, DateTime.Now.ToString("yyyy-MM-dd ") + line + Environment.NewLine); } catch { }
		}

		private void DiagEx(string where, Exception ex)
		{
			Diag("EXCEPTION in " + where + ": " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.ToString());
		}

		private GhostLimitState state = GhostLimitState.Disarmed;
		private bool isLong;
		private string errorReason = string.Empty;
		private bool mouseHooked;
		// Referencia capturada al enganchar: el desenganche en Terminated usa ESTA, no la
		// propiedad ChartControl, que en Terminated puede venir null y dejar el handler vivo.
		private ChartControl hookedChart;
		private ChartTrader chartTrader;

		private Account accountObj;
		private string armedAccountName = string.Empty;
		private string armedTemplate = string.Empty;
		private int armedQuantity;
		private Order entryOrder;
		private double sentPrice;
		private GhostLimitEntryType sentType;
		private DateTime triggeredAt;
		// v1.2.4 — ciclo de vida tras el fill: cuándo se llenó y si ya se vio la posición
		// abierta en la cuenta (ver GhostLimitRules.ShouldClearAfterClose).
		private DateTime filledAt;
		private bool sawPosition;
		private bool flatReadErrorLogged;

		private double lastPrice;
		private double prevPrice;

		// Snapshot of the Chart Trader selection, taken on the UI thread when Arm is pressed.
		// The armed order uses ONLY this snapshot: changing the Chart Trader afterwards does
		// not retarget a live arm (cancel and re-arm to pick up a new selection).
		private class ArmContext
		{
			public Account Account;
			public string Template;
			public int Quantity;
			public string Error;
		}

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

				EntryType									= GhostLimitEntryType.Stop;
				PlaySoundOnTrigger							= true;
			}
			else if (State == State.DataLoaded)
			{
				RemoveDrawObject(TagTrigger);
				RemoveDrawObject(TagEntry);
				Diag("v" + Version + " loaded on " + Instrument.FullName + " | ChartControl=" + (ChartControl == null ? "null" : "ok"));

				ChartControl cc = ChartControl;
				if (cc != null)
					cc.Dispatcher.InvokeAsync(() =>
					{
						try
						{
							// Si NT8 nos terminó antes de que corriera este InvokeAsync, no engancharse:
							// sería un handler fantasma desde el primer segundo.
							if (State == State.Terminated)
							{
								Diag("hook skipped: instance already terminated");
								return;
							}
							chartTrader = cc.OwnerChart != null ? cc.OwnerChart.ChartTrader : null;
							cc.PreviewMouseLeftButtonDown += OnChartClick;
							hookedChart = cc;
							mouseHooked = true;
							clickOwner[cc] = this;
							Diag("click handler hooked | chartTrader=" + (chartTrader == null ? "null" : "ok") + " | " + DescribeChartTraderSelection());
						}
						catch (Exception ex)
						{
							DiagEx("hook", ex);
						}
					});
			}
			else if (State == State.Terminated)
			{
				ReleaseArmOwnership();
				ChartControl cc = hookedChart;
				if (cc != null)
					cc.Dispatcher.InvokeAsync(() =>
					{
						cc.PreviewMouseLeftButtonDown -= OnChartClick;
						mouseHooked = false;
						hookedChart = null;
						GhostLimit owner;
						if (clickOwner.TryGetValue(cc, out owner) && ReferenceEquals(owner, this))
							clickOwner.Remove(cc);
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

				string button = rectLong.Contains(x, y) ? "Arm Long"
					: rectShort.Contains(x, y) ? "Arm Short"
					: rectCancel.Contains(x, y) ? "Cancel"
					: null;

				// Todo clic sobre el chart deja rastro con su geometría: si un botón "no hace
				// nada" hay que poder ver si el clic llegó y dónde cayó respecto a los botones.
				Diag(string.Format("click at ({0:0},{1:0}) scale={2:0.##} State={3} state={4} -> {5} | long={6} short={7} cancel={8} | src={9}",
					x, y, scaleX, State, state, button ?? "(no button)",
					RectStr(rectLong), RectStr(rectShort), RectStr(rectCancel),
					e.OriginalSource == null ? "null" : e.OriginalSource.GetType().Name));

				if (button == null)
					return;

				// Instancia fantasma (terminada, o desplazada por una recarga): deja pasar el
				// evento SIN marcarlo Handled para que lo reciba la instancia dueña del chart.
				GhostLimit owner = null;
				ChartControl cc = hookedChart;
				bool isOwner = cc != null && clickOwner.TryGetValue(cc, out owner) && ReferenceEquals(owner, this);
				if (State == State.Terminated || !isOwner)
				{
					Diag(string.Format("click on '{0}' passed on: this instance is {1} (stale handler after a chart reload).",
						button, State == State.Terminated ? "terminated" : "not the chart's click owner"));
					return;
				}

				e.Handled = true;
				Diag(string.Format("click '{0}' | state={1} | CanArm={2}", button, state, CanArm()));

				if (button == "Cancel")
				{
					if (state == GhostLimitState.Armed || state == GhostLimitState.Triggered)
						TriggerCustomEvent(o => { try { CancelPressed(); } catch (Exception ex) { DiagEx("CancelPressed", ex); } }, null);
				}
				else if (CanArm())
				{
					bool goLong = button == "Arm Long";
					ArmContext ctx = CaptureChartTraderContext();
					Diag("ctx captured | account=" + (ctx.Account == null ? "null" : ctx.Account.Name) + " | template=" + (ctx.Template ?? "null") + " | qty=" + ctx.Quantity + " | err=" + (ctx.Error ?? "none"));
					bool ran = false;
					TriggerCustomEvent(o =>
					{
						ran = true;
						try { Arm(goLong, ctx); }
						catch (Exception ex)
						{
							DiagEx("Arm", ex);
							try { SetError("Arm failed: " + ex.GetType().Name + ": " + ex.Message); } catch (Exception ex2) { DiagEx("SetError", ex2); }
						}
					}, null);
					Diag("TriggerCustomEvent returned | callback ran=" + ran + " | state now=" + state);
				}
			}
			catch (Exception ex)
			{
				DiagEx("OnChartClick", ex);
			}
		}

		private static string RectStr(SharpDX.RectangleF r)
		{
			return string.Format("[{0:0},{1:0} {2:0}x{3:0}]", r.X, r.Y, r.Width, r.Height);
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

		// Chart Trader is a WPF control: read it only from the UI thread (mouse handler
		// and OnRender both run there). The snapshot then travels to the NinjaScript
		// thread via TriggerCustomEvent.
		private ArmContext CaptureChartTraderContext()
		{
			ArmContext ctx = new ArmContext();
			try
			{
				if (chartTrader == null && ChartControl != null && ChartControl.OwnerChart != null)
					chartTrader = ChartControl.OwnerChart.ChartTrader;
				if (chartTrader == null)
				{
					ctx.Error = "Chart Trader was not found on this chart.";
					return ctx;
				}
				ctx.Account = chartTrader.Account;
				AtmStrategy sel = chartTrader.AtmStrategy;
				if (sel != null)
					ctx.Template = !string.IsNullOrWhiteSpace(sel.Template) ? sel.Template : sel.DisplayName;
				ctx.Quantity = chartTrader.Quantity;
			}
			catch (Exception ex)
			{
				ctx.Error = "Could not read the Chart Trader selection: " + ex.Message;
			}
			return ctx;
		}

		private string DescribeChartTraderSelection()
		{
			try
			{
				if (chartTrader == null)
					return "Chart Trader not found";
				Account a = chartTrader.Account;
				AtmStrategy sel = chartTrader.AtmStrategy;
				string atmName = sel == null ? "<None>" : (!string.IsNullOrWhiteSpace(sel.Template) ? sel.Template : sel.DisplayName);
				return (a == null ? "no account" : a.Name) + " | " + atmName + " | qty " + chartTrader.Quantity;
			}
			catch
			{
				return "Chart Trader unreadable";
			}
		}
		#endregion

		#region Arm / Cancel
		private void Arm(bool goLong, ArmContext ctx)
		{
			if (!CanArm())
				return;

			if (OtherInstanceIsLive())
			{
				SetError("Another GhostLimit instance already has a live order. Cancel it first.");
				return;
			}

			if (ctx == null || ctx.Error != null)
			{
				SetError(ctx == null ? "The Chart Trader selection could not be read." : ctx.Error);
				return;
			}

			if (ctx.Account == null)
			{
				SetError("Chart Trader has no account selected.");
				return;
			}

			if (string.IsNullOrWhiteSpace(ctx.Template))
			{
				SetError("Chart Trader ATM selector is <None>. Select an ATM template first (GhostLimit needs it for the brackets).");
				return;
			}

			string templatePath = Path.Combine(Core.Globals.UserDataDir, "templates", "AtmStrategy", ctx.Template + ".xml");
			if (!File.Exists(templatePath))
			{
				SetError("ATM template '" + ctx.Template + "' has no saved .xml (unsaved Custom ATM?). Save it as a template first.");
				return;
			}

			if (ctx.Quantity < 1 || ctx.Quantity > MaxQuantity)
			{
				SetError("Chart Trader quantity " + ctx.Quantity + " is outside the GhostLimit safety range (1-" + MaxQuantity + ").");
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
			Diag("Arm: checks passed, drawing lines trigger=" + trigger + " entry=" + entry);

			HorizontalLine lt = Draw.HorizontalLine(this, TagTrigger, trigger, Brushes.DarkOrange, DashStyleHelper.Dash, 2);
			lt.IsLocked = false;
			HorizontalLine le = Draw.HorizontalLine(this, TagEntry, entry, Brushes.DodgerBlue, DashStyleHelper.Solid, 2);
			le.IsLocked = false;
			Diag("Arm: lines drawn");

			accountObj = ctx.Account;
			armedAccountName = ctx.Account.Name;
			armedTemplate = ctx.Template;
			armedQuantity = ctx.Quantity;
			isLong = goLong;
			entryOrder = null;
			prevPrice = RoundTick(refPrice);
			errorReason = string.Empty;
			state = GhostLimitState.Armed;
			armOwner = this;

			Diag(string.Format("ARMED {0} | account={1} | ATM={2} | qty={3} | type={4} | trigger={5} | entry={6}",
				goLong ? "LONG" : "SHORT", armedAccountName, armedTemplate, armedQuantity, EntryType, trigger, entry));
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
			Diag("ERROR: " + reason);
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
			else if (state == GhostLimitState.PositionOpen)
				WatchPosition();

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
					armedQuantity, lim, stp, string.Empty, "Entry", Core.Globals.MaxDate, null);
				AtmStrategy atm = AtmStrategy.StartAtmStrategy(armedTemplate, entryOrder);
				if (atm == null)
				{
					entryOrder = null;
					RemoveLines();
					SetError("ATM strategy failed to start (template '" + armedTemplate + "'). Nothing was sent.");
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
				armedQuantity, Instrument.MasterInstrument.Name, entry, armedAccountName, armedTemplate));
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
				filledAt = DateTime.Now;
				sawPosition = false;
				flatReadErrorLogged = false;
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

		// v1.2.4 — con POSITION OPEN se vigila la posición de la cuenta armada para el
		// instrumento del chart, con el mismo mecanismo con el que se vigila el fill: en
		// cada tick, sin suscribirse a eventos de la cuenta (nada que desenganchar en
		// Terminated). Cuando vuelve a plana —TP, SL o Flatten manual, da igual— se borra
		// la línea azul de entrada (el gatillo ya se borró al disparar) y el indicador
		// queda DISARMED. Hasta ahora nadie borraba esa línea: quedaba como dibujo muerto.
		private void WatchPosition()
		{
			bool? flat = AccountIsFlat();
			if (flat == null)
				return; // lectura no fiable: nunca borrar por no poder mirar.

			if (!flat.Value)
			{
				sawPosition = true;
				return;
			}

			double sinceFill = (DateTime.Now - filledAt).TotalSeconds;
			if (!GhostLimitRules.ShouldClearAfterClose(state, sawPosition, true, sinceFill, GhostLimitRules.FlatGraceSeconds))
				return;

			entryOrder = null;
			RemoveLines();
			state = GhostLimitState.Disarmed;
			Print("[GhostLimit] " + DateTime.Now.ToString("HH:mm:ss") + " position on " + Instrument.FullName + " is FLAT (" + armedAccountName + "); entry line removed, disarmed.");
			Diag("position flat after fill | sawPosition=" + sawPosition + " | " + sinceFill.ToString("0.0") + "s since fill -> lines removed, DISARMED");
			ForceRefresh();
		}

		// true = la cuenta armada no tiene posición en el instrumento del chart;
		// false = la tiene; null = no se pudo leer (la colección cambió debajo, cuenta nula).
		// Solo mira la cuenta capturada al armar (R14): ninguna otra cuenta entra aquí.
		private bool? AccountIsFlat()
		{
			try
			{
				if (accountObj == null || Instrument == null)
					return null;
				string name = Instrument.FullName;
				foreach (Position pos in accountObj.Positions)
				{
					if (pos == null || pos.Instrument == null)
						continue;
					if (!string.Equals(pos.Instrument.FullName, name, StringComparison.OrdinalIgnoreCase))
						continue;
					if (pos.MarketPosition != MarketPosition.Flat && pos.Quantity != 0)
						return false;
				}
				return true;
			}
			catch (Exception ex)
			{
				// Una sola línea por ciclo: esto corre en cada tick y un fallo persistente
				// inundaría el fichero de diagnóstico.
				if (!flatReadErrorLogged)
				{
					flatReadErrorLogged = true;
					DiagEx("AccountIsFlat", ex);
				}
				return null;
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
						text = string.Format("ARMED {0} | {1} | {2} | qty {3} | {4}\nDrag the lines: orange = trigger, blue = entry.",
							isLong ? "LONG" : "SHORT", armedAccountName, armedTemplate, armedQuantity, EntryType);
						statusColor = new SharpDX.Color(255, 167, 38, 255);
						break;
					case GhostLimitState.Triggered:
						text = string.Format("TRIGGERED | {0} {1} {2} @ {3} (working, {4})\nMoving the blue line does NOT requote the order.",
							sentType, isLong ? "BUY" : "SELL", armedQuantity, sentPrice, armedAccountName);
						statusColor = new SharpDX.Color(66, 165, 245, 255);
						break;
					case GhostLimitState.PositionOpen:
						text = "POSITION OPEN | managed by ATM/Chart Trader. Entry line clears when flat. Arm again for a new order.";
						statusColor = new SharpDX.Color(102, 187, 106, 255);
						break;
					case GhostLimitState.Error:
						// The error sticks until the next Arm/Cancel; show the live selection
						// underneath so a fixed Chart Trader is visibly ready to re-arm.
						text = "ERROR: " + errorReason + "\nNext order -> " + DescribeChartTraderSelection() + " | press Arm to retry.";
						statusColor = new SharpDX.Color(239, 83, 80, 255);
						break;
					default:
						text = "GhostLimit v" + Version + " | DISARMED | use Arm Long / Arm Short.\nNext order -> " + DescribeChartTraderSelection();
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
		// v1.2: account, ATM template and quantity are no longer parameters — they are
		// captured from the Chart Trader selection at the moment Arm is pressed.
		[Display(Name = "Entry type", GroupName = "Parameters", Order = 1, Description = "Stop = enter on the comeback (confirmation). Limit = enter on the retest of the line.")]
		public GhostLimitEntryType EntryType { get; set; }

		[Display(Name = "Play sound on trigger", GroupName = "Parameters", Order = 2)]
		public bool PlaySoundOnTrigger { get; set; }
		#endregion
	}
}
