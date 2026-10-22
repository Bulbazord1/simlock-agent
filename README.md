# SimLock — agente de Windows

Overlay que corre en la PC de cada simulador y muestra el estado del turno que le manda Fore Booking.
Implementa el contrato `simlock-agent-api.md` (vinculación, `/state`, heartbeat, eventos, QR).

Es **un bloqueo visual**: tapa la pantalla cuando corresponde, pero no controla el simulador ni el teclado.

## Qué hay en el repo

| Carpeta | Qué es |
|---|---|
| `src/SimLock.Core` | Toda la lógica, sin interfaz: cliente HTTP, reloj del servidor, estados, avisos, cola de eventos, QR. Multiplataforma. |
| `src/SimLock.Agent` | La app de Windows (WPF, .NET 8): ventana del overlay, configuración con PIN, monitor, arranque automático. |
| `src/SimLock.Cli` | `simlock-cli`: herramienta de consola para probar el backend desde cualquier PC sin instalar el overlay. |
| `tests/SimLock.Core.Tests` | Pruebas del núcleo (38) + volcado de QR para verificarlos con `tools/verify_qr.py`. |

## Compilar

**En una PC con Windows** (necesita el SDK de .NET 8):

```powershell
dotnet run --project tests/SimLock.Core.Tests -c Release     # pruebas
dotnet publish src/SimLock.Agent -c Release -o publish        # → publish\SimLock.exe
```

Sale un único `SimLock.exe` autocontenido (no hace falta instalar .NET en la PC del simulador).

**Sin Windows:** subí el repo a GitHub; el workflow `.github/workflows/build.yml` compila en Windows, corre las pruebas y deja `SimLock.exe` y `simlock-cli.exe` como artefacto descargable.

## Instalar en la PC de un simulador

1. Copiar `SimLock.exe` a una carpeta fija, por ejemplo `C:\SimLock\`.
2. Ejecutarlo una vez. Queda configurado para arrancar con Windows (clave *Run* del usuario).
3. Se abre la configuración:
   - Definir el **PIN** (protege la configuración).
   - En el panel: *Simuladores → ícono SimLock de la bay → Vincular equipo*. Ingresar el código de 6 dígitos.
   - Elegir el **monitor** donde va el overlay y *Guardar*.
4. Configurar el simulador en **ventana sin bordes** ("borderless windowed"), no en pantalla completa exclusiva. Ver *Limitaciones*.

Para volver a la configuración: **Ctrl + Alt + Shift + S** y el PIN. Desde ahí también se desvincula o se cierra SimLock.

Archivos locales (`%LOCALAPPDATA%\SimLock`): `config.json`, `device.token` (cifrado con DPAPI; copiado a otra PC no sirve), último estado, cola de eventos y `logs\` (7 días).

## Qué muestra

| Estado | Pantalla | Clics |
|---|---|---|
| `disabled` | Nada | — |
| `idle` | **Reposo:** fondo negro con hora, nombre de la bay y logo tenue; el bloque cambia de lugar cada minuto | Bloquea |
| `upcoming` | "Hola, Juan — tu turno empieza a las 18:30" + QR de check-in | Bloquea |
| `awaiting_checkin` | "Hacé check-in para empezar" + QR | Bloquea |
| `in_progress` | Reloj chico arriba a la derecha | Pasan al juego |
| `last_minutes` | Tarjeta arriba a la derecha con la cuenta, la hora de fin y QR para extender | Pasan al juego |
| aviso de minutos | Cartel centrado 8 segundos ("Quedan 10 minutos" + QR), una vez por umbral | Pasan al juego |
| `ended` | "Tu turno terminó" + QR para extender si se puede, durante 3 minutos; después **reposo** (con QR chico mientras se pueda extender) | Bloquea |

Además: banner de `message`, "Pago pendiente — pasá por recepción" y un indicador de "Sin conexión".

## Probar el backend sin el overlay

```bash
simlock-cli pair 482913 --url https://panel.forebooking.com --name "PC prueba"
simlock-cli state     # un pedido a /state y cómo lo interpretaría el agente
simlock-cli watch     # en vivo, con avisos y el QR dibujado en la terminal
```

Corre en Windows, Mac o Linux (`dotnet run --project src/SimLock.Cli -- watch`). Usa exactamente el mismo núcleo que el overlay.
`state` además avisa si `remainingSeconds` del servidor no coincide con `endsAt - serverTime`.

## Decisiones sobre puntos que el contrato no define

- **Avisos después de extender:** el contrato dice "un aviso por `booking.id + minutos`". Se usa `booking.id + endsAt + minutos`, así después de una extensión el cliente vuelve a recibir los avisos de 10 y 5 minutos del nuevo fin.
- **Entre consultas y sin conexión** el agente avanza solo con horarios conocidos: pasa a "últimos minutos" al cruzar un umbral y a "terminado" en `endsAt`. Nunca alarga un turno ni lo termina antes del `endsAt` conocido, y nunca retrocede un estado que mandó el servidor.
- **Arranque sin internet:** si la PC reinicia sin red, se retoma el último estado guardado (si tiene menos de 6 h) estimando la hora con el reloj de Windows. Es el único caso en que se usa ese reloj, y solo hasta la primera respuesta del servidor.
- **`started`** se envía cuando el turno empieza a mostrarse en curso (una vez por reserva).
- **Heartbeat:** al arrancar y cada 10 minutos, porque `/state` no informa la versión instalada.
- **Mientras la configuración está abierta** el overlay se oculta, para no tapar la ventana.

## Limitaciones conocidas

- **Pantalla completa exclusiva (DirectX):** Windows puede no dibujar ninguna ventana encima de un juego en ese modo. Hay que configurar el simulador en ventana sin bordes. **Es lo primero que hay que probar en una bay real.**
- **Bloqueo solo visual:** con Alt+Tab o el Administrador de tareas se puede cerrar o esquivar. El teclado siempre sigue yendo al simulador.
- **Un solo proceso** (como pide el contrato): si alguien lo cierra desde el Administrador de tareas, vuelve recién al reiniciar o al volver a iniciar sesión.
- **Actualización automática: no implementada.** El contrato la pide, pero no define de dónde bajar las versiones. Hace falta un endpoint (por ejemplo `GET /api/public/simlock/latest` con versión, URL y SHA-256) para agregarla.
- **Sin firma de código:** Windows SmartScreen va a advertir la primera vez que se ejecute.
