// Arnés de pruebas de GhostLimit — corre SIN NinjaTrader y SIN mercado abierto.
//
// Prueba la lógica REAL del indicador (llama a los métodos estáticos de GhostLimit.cs
// compilado), no una copia. Cubre lo que no se puede validar a mano en el chart:
// la tabla exhaustiva fail-closed (spec R8) y la detección de toque con gaps (R6).
//
// Correr: scripts/ninjascript/tests/correr_pruebas_ghostlimit.ps1

// El alias GL apunta al GhostLimit.dll recién compilado desde el fuente del repo.
// Es obligatorio: NinjaTrader.Custom.dll también trae un GhostLimit (el que compila NT8
// con F5) y sin el alias los dos tipos chocan (CS0433). Con el alias probamos siempre
// el código del repo, nunca el binario que quedó dentro de NinjaTrader.
extern alias GL;

using System;
using GL::NinjaTrader.NinjaScript.Indicators;

internal static class GhostLimitTests
{
	private const double Entry = 27500.00;
	private const double Below = 27495.00;
	private const double Equal = 27500.00;
	private const double Above = 27505.00;

	private static int passed;
	private static int failed;

	private static int Main()
	{
		Console.WriteLine("=== GhostLimit — pruebas sin mercado ===");
		Console.WriteLine();

		Console.WriteLine("R8 · tabla fail-closed (12 casos)");
		// STOP LONG: solo válido si el precio viene por DEBAJO de la entrada.
		Reject(true, GhostLimitEntryType.Stop, Below, "stop long, precio debajo -> se envia", expectReject: false);
		Reject(true, GhostLimitEntryType.Stop, Equal, "stop long, precio igual -> bloquea");
		Reject(true, GhostLimitEntryType.Stop, Above, "stop long, precio arriba -> bloquea");
		// STOP SHORT: solo válido si el precio viene por ARRIBA de la entrada.
		Reject(false, GhostLimitEntryType.Stop, Above, "stop short, precio arriba -> se envia", expectReject: false);
		Reject(false, GhostLimitEntryType.Stop, Equal, "stop short, precio igual -> bloquea");
		Reject(false, GhostLimitEntryType.Stop, Below, "stop short, precio debajo -> bloquea");
		// LIMIT LONG: se llenaría al instante si el precio ya está por debajo.
		Reject(true, GhostLimitEntryType.Limit, Above, "limit long, precio arriba -> se envia", expectReject: false);
		Reject(true, GhostLimitEntryType.Limit, Equal, "limit long, precio igual -> se envia (retest)", expectReject: false);
		Reject(true, GhostLimitEntryType.Limit, Below, "limit long, precio debajo -> bloquea (marketable)");
		// LIMIT SHORT: se llenaría al instante si el precio ya está por arriba.
		Reject(false, GhostLimitEntryType.Limit, Below, "limit short, precio debajo -> se envia", expectReject: false);
		Reject(false, GhostLimitEntryType.Limit, Equal, "limit short, precio igual -> se envia (retest)", expectReject: false);
		Reject(false, GhostLimitEntryType.Limit, Above, "limit short, precio arriba -> bloquea (marketable)");

		Console.WriteLine();
		Console.WriteLine("R6 · deteccion de toque (8 casos)");
		Touch(27490, 27495, 27500, false, "sube pero no llega al gatillo");
		Touch(27490, 27500, 27500, true, "toca el gatillo exacto subiendo");
		Touch(27490, 27512, 27500, true, "GAP que salta el gatillo subiendo");
		Touch(27510, 27505, 27500, false, "baja pero no llega al gatillo");
		Touch(27510, 27500, 27500, true, "toca el gatillo exacto bajando");
		Touch(27510, 27488, 27500, true, "GAP que salta el gatillo bajando");
		Touch(27505, 27510, 27500, false, "ya estaba arriba y sigue subiendo");
		Touch(27495, 27490, 27500, false, "ya estaba abajo y sigue bajando");

		Console.WriteLine();
		Console.WriteLine("Colocacion inicial de las lineas (50 puntos de separacion)");
		Placement();

		Console.WriteLine();
		Console.WriteLine("v1.2.4 · la linea de entrada se borra al quedar plana la posicion (10 casos)");
		Lifecycle();

		Console.WriteLine();
		Console.WriteLine(string.Format("=== {0} passed, {1} failed ===", passed, failed));
		return failed == 0 ? 0 : 1;
	}

	// Ciclo de vida de la linea azul tras el fill. La regla es la que corre en cada tick
	// con POSITION OPEN; aqui se le dan las lecturas que veria en el chart.
	private static void Lifecycle()
	{
		double grace = GhostLimitRules.FlatGraceSeconds;

		// Lo que Kevin usa: armada sin ejecutar, la cuenta esta plana porque NO hay orden
		// en el mercado. La linea tiene que quedarse pase el tiempo que pase.
		Clear(GhostLimitState.Armed, false, true, 0.0, false, "ARMED sin ejecutar, cuenta plana -> la linea se queda");
		Clear(GhostLimitState.Armed, false, true, 3600.0, false, "ARMED una hora sin tocar el gatillo -> la linea se queda");
		// Disparada y trabajando en el broker: sigue sin posicion, la linea se queda.
		Clear(GhostLimitState.Triggered, false, true, 60.0, false, "TRIGGERED trabajando, cuenta plana -> la linea se queda");
		// Sin orden viva no hay nada que borrar por este camino.
		Clear(GhostLimitState.Disarmed, true, true, 60.0, false, "DISARMED -> no aplica");
		Clear(GhostLimitState.Error, true, true, 60.0, false, "ERROR -> no aplica");

		// Entrada ejecutada, posicion vista abierta, luego plana: TP, SL o Flatten manual
		// se ven identicos desde la cuenta. Se borra sin esperar gracia.
		Clear(GhostLimitState.PositionOpen, true, true, 0.5, true, "ejecutada, posicion vista, vuelve plana -> se borra (TP/SL/Flatten)");
		// Posicion todavia abierta: la linea se queda mientras el ATM gestiona.
		Clear(GhostLimitState.PositionOpen, true, false, 600.0, false, "ejecutada, posicion sigue abierta -> la linea se queda");
		// Recien llenada y la cuenta aun no refleja la posicion: NO borrar (falso plano).
		Clear(GhostLimitState.PositionOpen, false, true, grace - 1.0, false, "recien llenada, cuenta aun plana dentro de la gracia -> no borra");
		// Entrada y stop entre dos ticks: nunca se vio la posicion, pero pasada la gracia
		// plana es plana.
		Clear(GhostLimitState.PositionOpen, false, true, grace, true, "nunca se vio la posicion, plana pasada la gracia -> se borra");
		Clear(GhostLimitState.PositionOpen, false, true, grace + 30.0, true, "nunca se vio la posicion, plana mucho despues -> se borra");
	}

	private static void Clear(GhostLimitState state, bool sawPosition, bool isFlatNow, double secondsSinceFill, bool expected, string caso)
	{
		bool actual = GhostLimitRules.ShouldClearAfterClose(state, sawPosition, isFlatNow, secondsSinceFill, GhostLimitRules.FlatGraceSeconds);
		Report(actual == expected, caso, expected ? "borra" : "se queda", actual ? "borra" : "se queda");
	}

	private static void Placement()
	{
		const double Price = 27500.00;
		double sep = GhostLimitRules.DefaultSeparationPoints;
		double trigger, entry;

		GhostLimitRules.DefaultLines(Price, false, sep, out trigger, out entry);
		Report(Math.Abs((trigger - entry) - 50.0) < 1e-9, "short: separacion de 50 puntos exactos", "50", (trigger - entry).ToString("0.##"));
		Report(trigger > Price && entry < Price, "short: gatillo arriba y entrada abajo del precio", "arriba/abajo",
			string.Format("{0:0.##}/{1:0.##}", trigger, entry));
		// Al tocar el gatillo el precio queda 50 pts arriba de la entrada -> sell stop valido.
		Report(GhostLimitRules.ValidateEntry(false, GhostLimitEntryType.Stop, trigger, entry) == null,
			"short: al tocar el gatillo el sell stop es valido", "enviado", "bloqueado");

		GhostLimitRules.DefaultLines(Price, true, sep, out trigger, out entry);
		Report(Math.Abs((entry - trigger) - 50.0) < 1e-9, "long: separacion de 50 puntos exactos", "50", (entry - trigger).ToString("0.##"));
		Report(trigger < Price && entry > Price, "long: gatillo abajo y entrada arriba del precio", "abajo/arriba",
			string.Format("{0:0.##}/{1:0.##}", trigger, entry));
		Report(GhostLimitRules.ValidateEntry(true, GhostLimitEntryType.Stop, trigger, entry) == null,
			"long: al tocar el gatillo el buy stop es valido", "enviado", "bloqueado");
	}

	private static void Reject(bool isLong, GhostLimitEntryType type, double touchPrice, string caso, bool expectReject = true)
	{
		string result = GhostLimitRules.ValidateEntry(isLong, type, touchPrice, Entry);
		bool rejected = result != null;
		Report(rejected == expectReject, caso, expectReject ? "bloqueado" : "enviado", rejected ? "bloqueado" : "enviado");
	}

	private static void Touch(double prev, double price, double trigger, bool expected, string caso)
	{
		bool actual = GhostLimitRules.IsTouched(prev, price, trigger);
		Report(actual == expected, caso, expected ? "dispara" : "no dispara", actual ? "dispara" : "no dispara");
	}

	private static void Report(bool ok, string caso, string esperado, string obtenido)
	{
		if (ok)
		{
			passed++;
			Console.WriteLine("  PASS  " + caso);
		}
		else
		{
			failed++;
			Console.WriteLine(string.Format("  FAIL  {0} | esperaba '{1}', obtuvo '{2}'", caso, esperado, obtenido));
		}
	}
}
