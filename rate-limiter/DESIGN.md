# Diseño

Rate limiter del lado del servidor, basado en el capítulo 4 de *System Design Interview* (Alex Xu, vol. 1). Las páginas citadas son las de ese capítulo.

## Alcance

- **Una sola instancia, estado en memoria.** No soporta varias instancias detrás de un load balancer: cada una contaría por su lado. Es una decisión consciente para un prototipo (más abajo, en "Qué cambiaría para varias instancias").
- **Middleware dentro de la API**, no un servicio aparte. El libro propone el rate limiter como middleware del lado del servidor (págs. 53–54); del lado del cliente no protege, porque cualquiera lo saltea con un script.
- La clave del cliente es la **IP**, porque la API no tiene autenticación.
- Dos algoritmos: **token bucket** y **sliding window counter**.

## Arquitectura

Cuando llega un request, el routing de ASP.NET Core resuelve qué endpoint lo atiende. Después corre `RateLimitingMiddleware`:

1. Si el endpoint no tiene `[RateLimit]`, el request sigue de largo.
2. Si lo tiene, busca la regla por nombre en `RateLimitRules` y le pide al algoritmo de esa regla una decisión para la IP del cliente.
3. Si pasa, agrega los headers `X-Ratelimit-*` y sigue al controller; si no, responde 429.

En paralelo, `IdleBucketCleanupService` le pide cada tanto a cada algoritmo que borre a los clientes inactivos.

El código está en dos proyectos:

- **`RateLimiter.Core`**: los algoritmos detrás de una interfaz chica, `IRateLimiter` (`TryAcquire` y `RemoveIdle`). No dependen de HTTP.
  - Cada implementación esconde su estado, su lock y su matemática.
  - El estado por cliente (`Bucket`, `Counters`) está en su propio archivo con `partial class`, así sigue siendo una clase privada del algoritmo. Cada algoritmo tiene su carpeta (`TokenBucket/`, `SlidingWindowCounter/`).
- **`RateLimiter.Api`**:
  - `RateLimitingMiddleware`: lee la regla del endpoint, obtiene la IP, le pregunta al algoritmo y responde.
  - `[RateLimit("nombre")]`: indica qué regla usa cada endpoint.
  - `RateLimitRules`: lee y valida las reglas de `appsettings.json`, con un algoritmo por regla.
  - `IdleBucketCleanupService`: borra periódicamente los clientes inactivos.

## Decisiones y trade-offs

### Algoritmos: token bucket + sliding window counter

- **Token bucket** (pág. 54): lo usan Amazon y Stripe, es simple y **permite ráfagas** mientras queden tokens.
  - La recarga es continua y proporcional al tiempo transcurrido.
  - No hay un proceso que recargue los baldes, como el de la Fig. 4-6: cada balde calcula su recarga cuando llega un request.
  - `Limit` es la capacidad y `Window` el tiempo que tarda en llenarse desde vacío.
- **Sliding window counter** (pág. 61): corrige la ráfaga en el borde del fixed window (Fig. 4-9, hasta el doble de lo permitido) guardando **dos contadores por cliente**, no un timestamp por request.
  - Es una aproximación: supone que los requests de la ventana anterior estaban repartidos parejo. Cloudflare midió un 0,003% de error.
  - Se redondea para abajo, como en el ejemplo del libro (`3 + 5 × 0,7 = 6,5 → 6`).
  - Las ventanas están alineadas al reloj: una ventana de 1 minuto es un minuto redondo.
  - Los rechazados no cuentan.

Descartados:
- **Leaking bucket**: encola requests y los procesa a ritmo fijo. En un middleware que tiene que responder en el momento (pasa o 429) agrega latencia y una cola con un worker.
- **Fixed window counter**: tiene el problema del borde de ventana, que es justo lo que resuelve el sliding window counter.
- **Sliding window log**: es el más preciso, pero guarda un timestamp por request, incluso de los rechazados. Es caro en memoria.

Los dos algoritmos están detrás de `IRateLimiter` porque hay dos implementaciones reales en uso. No hay más capas: no hay interfaz de "store", porque con Redis la lógica atómica de cada algoritmo cambiaría igual (ver abajo).

### Concurrencia: lock por cliente

ASP.NET Core atiende requests en paralelo en varios hilos. Recargar, chequear y descontar es un read-modify-write: sin exclusión mutua, dos requests del mismo cliente leen el mismo valor y pasa uno de más (pérdida de suma o *lost update*, Fig. 4-14).

- Los baldes viven en un `ConcurrentDictionary`, y cada balde se protege con su propio `lock`. Los clientes distintos no compiten entre sí; un lock global los haría hacer fila.
- La sección crítica es una cuenta sin I/O. No puede haber deadlock, porque nunca se toma más de un lock a la vez.
- El libro dice que los locks son lentos (pág. 66), pero se refiere a locks distribuidos sobre Redis. En memoria y sin contención, el costo es despreciable.
- **Test:** 1000 requests en paralelo contra un límite de 100 tienen que dar exactamente 100. Se verificó que, sacando el `lock`, el test falla (pasan 104).

### Reglas: configuración con nombre + attribute en el endpoint

- Los límites están en `appsettings.json`, con la idea de los archivos de reglas de Lyft (pág. 64): se cambian sin recompilar. Cada regla tiene sus propios contadores.
- Cada endpoint declara su regla con `[RateLimit("login")]`. En el controller aplica a todos sus endpoints, que comparten el límite; si también está en el método, gana el del método. Un endpoint sin el attribute no tiene límite.
- `Algorithm` es un string y no un enum, para poder dar un error que liste los valores válidos si está mal escrito.
- **Fail fast:** al arrancar se valida la config (límite y ventana mayores a cero, algoritmo conocido) y que todo `[RateLimit]` use una regla existente. Un typo frena el deploy, en lugar de dejar un endpoint sin protección.
- Las reglas se leen al arrancar, sin los workers de la Fig. 4-13 ni recarga en caliente. Recargarlas obligaría a decidir qué pasa con los baldes existentes cuando cambia un límite.

### Por qué metadata del endpoint y no rutas

La alternativa era poner la ruta en la config (`"Path": "/api/orders"`) y que el middleware comparara la URL de cada request. Lo descarté:

- **Comparar URLs es frágil.** `/api/orders/42` no es igual a `/api/orders`. Habría que manejar parámetros, prefijos, mayúsculas y barras finales, o sea, reimplementar el routing de ASP.NET Core, que ya lo resuelve bien.
- **El routing ya decidió.** Cuando corre el middleware, el request ya tiene un endpoint asignado. Leer su metadata es preguntar "¿este endpoint tiene regla?", sin saber nada de URLs.
- **Se ve al leer el código.** El `[RateLimit]` está al lado del endpoint, como `[HttpPost]` o `[Authorize]`. Con rutas en la config, para saber si un endpoint está limitado habría que ir a buscar al JSON.
- **Permite validar al arrancar.** Los nombres usados en los `[RateLimit]` se pueden leer de los endpoints antes de atender requests, y es la misma metadata que después lee el middleware. Con rutas en la config, un patrón mal escrito no coincide con nada y falla en silencio: el endpoint queda sin límite.

**Trade-off:** el "cuánto" (algoritmo, límite, ventana) se cambia en la config sin recompilar, pero el "dónde" (qué endpoints tienen límite) requiere cambiar código. Me parece correcto: agregar o sacar un límite a un endpoint es una decisión del endpoint, y la mantiene junto a él.

### Respuesta: 429 + headers (págs. 64–65)

- Todas las respuestas de un endpoint limitado llevan `X-Ratelimit-Limit` y `X-Ratelimit-Remaining`. Las rechazadas llevan además `X-Ratelimit-Retry-After` y un cuerpo JSON (`error`, `rule`, `retryAfterSeconds`).
- `Retry-After` se redondea **para arriba**, así el cliente nunca reintenta antes de tiempo. Los algoritmos calculan el instante exacto: en el sliding window, en el instante límite el estimado es igual al límite y todavía se rechaza, por eso se suma 1 tick.
- Los rechazados se descartan; no se encolan para después. Encolarlos depende del caso de uso y requiere cola, worker y respuesta asíncrona.

### Memoria: limpieza de clientes inactivos

Un diccionario en memoria no expira entradas como `EXPIRE` de Redis (pág. 62). Sin limpieza, cada IP nueva queda para siempre, y alguien que va cambiando de IP llena la memoria.

- Un `BackgroundService` con un `PeriodicTimer` llama cada `CleanupInterval` a `RemoveIdle` de cada algoritmo.
- Cada algoritmo borra solo los clientes cuyo estado **ya es idéntico al de uno nuevo**:
  - en token bucket, inactivo durante una recarga completa, porque el balde ya está lleno;
  - en sliding window, dos ventanas sin requests, porque los dos contadores están en 0.

  Borrarlos no le regala ni le quita requests a nadie, así que el umbral no es configurable.
- La limpieza corre en paralelo con los requests y no usa un lock propio: borra adentro del lock de cada balde y lo marca `Removed`. Un request que tenía ese balde en la mano lo ve marcado y vuelve a buscarlo (`TryAcquire` es un `while (true)` que casi siempre sale en la primera vuelta). Sin la marca, ese request descontaría de un balde huérfano y el siguiente crearía uno lleno: el cliente ganaría un request.

### Manejo de errores

- **Fail open:** si el limiter tira una excepción inesperada, se loguea como `Error` y el request pasa. El libro pide que un problema del limiter no tumbe el sistema (pág. 52). El costo es que, mientras falla, no protege.
- Los errores de configuración no son fail open: frenan el arranque, con un mensaje que dice qué valor está mal.
  - Las reglas se leen una por una a propósito. Leídas todas juntas como diccionario, .NET descarta en silencio una regla con un valor mal escrito (por ejemplo `"Window": "abc"`), y el error terminaba diciendo que la regla no existe.
  - `CleanupInterval` se valida al construir el servicio de limpieza, antes de que el servidor atienda requests. Validado más tarde, un valor de 0 tiraba la app ya en marcha.
- Si la IP llega vacía, se usa la clave `unknown`.
- Un error en la limpieza se loguea y se reintenta en el siguiente ciclo, sin tumbar la app.
- Timeouts, pools de conexiones y circuit breakers no aplican: no hay llamadas de red ni dependencias externas. Aparecerían con Redis (ver abajo).

### Tiempo inyectable

Los algoritmos reciben un `TimeProvider` en lugar de usar `DateTime.Now`. Los tests usan `FakeTimeProvider` y adelantan el tiempo a mano: son deterministas y no esperan.

### Observabilidad

- Se loguean los rechazos (como `Information`, con regla e IP) y los errores. Los permitidos no, porque serían ruido y en un ataque llenarían el log.
- Métricas `ratelimiter.allowed` y `ratelimiter.rejected` por regla, con `System.Diagnostics.Metrics`.
  - Alcanzan para ver si una regla está mal calibrada (pág. 68).
  - Se ven con `dotnet-counters` y se pueden exportar a Prometheus u OpenTelemetry sin cambiar código.
- Los mensajes de excepciones, logs y métricas están en inglés.

### Lo que no usé

- `Microsoft.AspNetCore.RateLimiting` (`AddRateLimiter`): ASP.NET Core ya trae un rate limiter. No lo usé porque el objetivo era implementarlo. En un proyecto real lo evaluaría primero.

## Tests

- **Algoritmos:** los ejemplos del libro (Fig. 4-6 y `6,5 → 6` de la Fig. 4-11), la ráfaga en el borde de ventana, el desborde de tokens, `RetryAfter` en los dos casos del sliding window, que los rechazados no cuenten, un reloj que se atrasa y vuelve, y la limpieza.
- **Concurrencia:** 1000 requests en paralelo contra un límite de 100, con los dos algoritmos y repetido 20 veces; y lo mismo con la limpieza corriendo sin parar en otro hilo.
- **Reglas:** validación de la config, typo en `[RateLimit]` y contadores separados por regla.
- **Integración** (`WebApplicationFactory`, la API entera en memoria): 429, headers, cuerpo JSON, redondeo de `Retry-After`, attribute a nivel de controller, endpoint sin límite, fail open y que la app no arranque con una config inválida.

## Despliegue

- Se levanta con `dotnet run` (comandos en el [README](README.md)). No requiere infraestructura externa.
- No incluí Dockerfile porque no tenía Docker para probarlo.

## Limitaciones conocidas

- **Una sola instancia** (ver abajo).
- **IP como clave:**
  - Detrás de un proxy o load balancer, la app vería la IP del proxy. Habría que usar `X-Forwarded-For` (middleware `ForwardedHeaders`), confiando solo en proxies conocidos.
  - Con NAT, varias personas comparten una IP y el límite.
- Si el reloj del sistema se atrasa, los algoritmos ignoran el retroceso: no restan tokens, no resetean ventanas y no mueven hacia atrás la última recarga (al volver el reloj, eso regalaría requests). Mientras dura el ajuste, el cálculo es aproximado.
- No se envía el header estándar de HTTP `Retry-After`, solo `X-Ratelimit-Retry-After`, como en el libro.
- No hay un test del `IdleBucketCleanupService` en sí: se testea `RemoveIdle` de cada algoritmo.
- No hay dashboards ni alertas.

## Qué cambiaría para varias instancias

Con varias instancias, cada una tendría sus propios contadores y nadie vería el total del cliente (problema de sincronización, Fig. 4-15). La solución del libro es centralizar el estado en Redis (Fig. 4-16):

- Nuevas implementaciones de `IRateLimiter` sobre Redis, con la lógica de cada algoritmo en un **script Lua**, que Redis ejecuta de forma atómica. El `lock` local ya no sirve entre procesos.
- `TryAcquire` pasaría a ser asíncrono por la llamada de red. En el middleware solo se agregaría un `await`.
- `RemoveIdle` no haría nada: cada clave tendría `EXPIRE`.
- Timeout corto y fail open si Redis no responde; opcionalmente, un circuit breaker.
- Usar la hora del servidor Redis, no la de cada instancia, para que las ventanas coincidan.
- A mayor escala, el libro propone varios data centers con edge servers cerca de los usuarios y sincronizar los contadores con consistencia eventual (pág. 67). El costo es precisión: durante un rato, un cliente podría pasarse un poco del límite.

No lo implementé porque el enunciado pide un prototipo sin sobreingeniería, y el diseño deja el cambio acotado a nuevas implementaciones de la interfaz.

## Cómo usé IA

Usé Claude Code en todo el proceso:

- **Entender el problema:** resumí el capítulo y el enunciado, y aclaré conceptos: rate limiting del lado del cliente o del servidor, sincronización entre instancias o race condition, y si hacía falta Redis. Relacioné el `lock` con lo que vi de programación concurrente (mutex, sección crítica).
- **Diseñar:** comparé alternativas con la IA (memoria o Redis, metadata o rutas, interfaz de store sí o no, qué algoritmos). Las decisiones las tomé yo y las registré antes de escribir código. En el camino descarté ideas de un primer borrador: un servicio aparte estilo gateway, una clave por cliente y endpoint, y una interfaz de store para cambiar a Redis.
- **Programar:** la IA (Claude Code) escribió el código a partir de esas decisiones, con comentarios aclarando el flujo del código para cada clase y configuración. Lo recorrí archivo por archivo preguntando lo que no entendía (inyección de dependencias, `RequestDelegate`, metadata de endpoints, binding de la configuración). Después pedí cambios concretos: separar `Bucket` y `Counters`, un `switch` clásico, mensajes en inglés.
- **Revisar:** le pedí revisar el código contra el enunciado y el capítulo. Encontró tres bugs, que se corrigieron con su test:
  - una regla con un valor inválido se descartaba en silencio;
  - un `CleanupInterval` de 0 tiraba la app después de arrancar;
  - el 429 no tenía cuerpo JSON.

  En una segunda revisión apareció uno más: si el reloj se atrasaba y volvía, el token bucket recargaba el balde de nuevo.
- **Verificar:**
  - los tests reproducen los ejemplos del libro;
  - saqué el `lock` a propósito para confirmar que el test de concurrencia detecta la pérdida de suma (pasaron 104 en lugar de 100);
  - comprobé las métricas con `dotnet-counters` contra la API real, y los 429 con `curl`.

