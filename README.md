# GhostLimit

**A resting order that isn't on the book.** Drag a trigger line and an entry line onto your
NinjaTrader 8 chart, arm it, and nothing is sent to the exchange. The moment price *touches*
the trigger, GhostLimit submits the real Stop or Limit entry with a native ATM template
attached — brackets included.

<!-- Add the demo here once recorded: ![GhostLimit demo](docs/demo.gif) -->

## Why it exists

A conventional sell limit fills the moment price reaches it — usually mid-push, right before
the move that takes out your stop. What actually worked for me was waiting for price to *tap*
a level (a VWAP, a prior high) and entering on the way back. Doing that by hand means staring
at the screen with the mouse hovering over the DOM.

GhostLimit is that pattern as an indicator: the level is armed, the order stays invisible
until the tap, and then it goes in as a stop entry on the pullback.

## How it works

| | |
|---|---|
| **Trigger line** (orange, dashed) | the price that wakes the order up |
| **Entry line** (blue) | where the order is actually placed |
| **Entry type** | `Stop` (default) or `Limit` |
| **Brackets** | always a native NT8 ATM template — no hand-rolled OCO |
| **Lifecycle** | one shot per arm: `DISARMED → ARMED → TRIGGERED → POSITION OPEN` |

Both lines are draggable. On load they appear 50 points apart, 25 either side of price, so
there is room to place them before arming.

**Fail-closed by design.** If, at the instant of the trigger, the order would be invalid or
immediately marketable (a "limit" that would fill instantly at the wrong price), nothing is
sent — the state goes to `ERROR` and says why. Cancellation is surgical, by order reference:
`CancelAllOrders` is never called, because in NinjaTrader it cancels orders across *every*
account, not just yours.

## Install

1. Copy `src/GhostLimit.cs` to
   `Documents\NinjaTrader 8\bin\Custom\Indicators\GhostLimit.cs`
2. In NinjaTrader press **F5** (compiling from the NinjaScript editor is what builds it — a
   machine restart is not enough).
3. Add **GhostLimit** to a chart. The `DISARMED` state shows a version stamp; check it after
   every recompile so you never test a stale binary.
4. Set the parameters: `AccountName` (**default `SimBot` — change it to your own sim account
   first**), `AtmTemplate`, `EntryType`, `Quantity`, `PlaySoundOnTrigger`.

The ATM template name must match a file in
`Documents\NinjaTrader 8\templates\AtmStrategy\` exactly — the `...` in names like
`60 . . . 40` are literal characters.

Requirements: NinjaTrader 8, an ATM template, and an account you're willing to send orders
from. Test on a simulation account first.

## Tests

The pure decision logic lives in `GhostLimitRules` — no state, no NinjaTrader types — so it
can be tested with the market closed and NinjaTrader shut:

```powershell
powershell -File tests\correr_pruebas_ghostlimit.ps1
```

20 cases (full truth table for the marketable/invalid guard, plus touch detection across
gaps). Exit `0` green, `1` a failing test, `2` a compile error. `tools\compilar_ninjascript.ps1`
compile-checks any NinjaScript file against the real NinjaTrader DLLs.

## Two gotchas that cost me hours

- **The order's `name` must be exactly `"Entry"`.** With any other name,
  `AtmStrategy.StartAtmStrategy` fails *silently*: the order sits in `Initialized` forever,
  shows on the chart as an uncancellable working order, and never reaches the simulator.
- **Recompiling does not reload the instance on your chart.** The old instance stays alive and
  can still fire with the previous binary. Disarm before pressing F5, and check the version
  stamp afterwards.

## Known limitations (v1)

State advances on the next incoming tick (polled in `OnMarketData`); removing the indicator
while an order is working does *not* cancel it (it stays as a manual order with its ATM);
dragging the entry line after the trigger does not re-quote; an armed state does not survive
a NinjaTrader restart — by design.

## License

MIT — see [LICENSE](LICENSE). Trading software provided as-is, with no warranty: it sends
real orders, and you are responsible for every one of them. Nothing here is financial advice.

---

## Español

**Una orden en reposo que no está en el libro.** Arrastras una línea de gatillo y una de
entrada sobre tu gráfica de NinjaTrader 8, armas, y no se manda nada al mercado. En el momento
en que el precio *toca* el gatillo, GhostLimit envía la entrada real —Stop o Limit— con una
plantilla ATM nativa: brackets incluidos.

**Por qué existe.** Un sell limit convencional se llena justo cuando el precio llega, en plena
subida, y el siguiente empujón te toca el stop. Lo que sí funcionaba era esperar a que el
precio *tocara* el nivel (una VWAP, un máximo previo) y entrar en el regreso — pero eso exige
estar pegado a la pantalla con el mouse sobre el DOM. GhostLimit convierte ese patrón en un
indicador.

**Cómo funciona.** Línea de gatillo (naranja, punteada) = el precio que despierta la orden;
línea de entrada (azul) = donde se coloca de verdad. `Stop` (default) o `Limit`. Brackets
siempre por ATM nativo. Un disparo por armado: `DISARMED → ARMED → TRIGGERED → POSITION OPEN`.
Las dos líneas son arrastrables y nacen a 50 puntos de distancia, 25 a cada lado del precio.

**Fail-closed.** Si al momento del disparo la orden sería inválida o marketable, no se envía
nada: estado `ERROR` y el motivo. La cancelación es quirúrgica por referencia de orden; jamás
`CancelAllOrders`, que en NinjaTrader cancela en **todas** las cuentas.

**Instalación.** Copia `src/GhostLimit.cs` a
`Documents\NinjaTrader 8\bin\Custom\Indicators\`, presiona **F5** en NinjaTrader, agrega
**GhostLimit** a una gráfica y ajusta `AccountName` (**default `SimBot`: cámbialo por tu
cuenta de simulación**), `AtmTemplate`, `EntryType`, `Quantity`. El nombre de la plantilla ATM
debe coincidir con el archivo en `templates\AtmStrategy\` (los `...` son literales).

**Pruebas.** `powershell -File tests\correr_pruebas_ghostlimit.ps1` corre 20 casos con el
mercado cerrado y NinjaTrader apagado (las reglas puras viven en `GhostLimitRules`).

**Dos trampas que costaron horas.** El `name` de la orden debe ser exactamente `"Entry"` o
`StartAtmStrategy` falla en silencio y la orden queda `Initialized` para siempre. Y recompilar
**no** recarga la instancia de tu gráfica: desarma antes del F5 y verifica el sello de versión.

**Limitaciones v1.** El estado avanza con el siguiente tick; quitar el indicador con una orden
viva no la cancela; arrastrar la línea azul después del disparo no re-cotiza; el armado no
sobrevive a un reinicio de NinjaTrader (a propósito).

Licencia MIT. Software de trading tal cual, sin garantía: manda órdenes reales y tú eres
responsable de todas. Esto no es asesoría financiera.
