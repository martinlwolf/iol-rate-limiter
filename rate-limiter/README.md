# Rate limiter

Prototipo de un rate limiter del lado del servidor en C# / ASP.NET Core (.NET 10), basado en el capítulo 4 de *System Design Interview* (Alex Xu). Implementa dos algoritmos, **token bucket** y **sliding window counter**, en un middleware que devuelve **429** con headers `X-Ratelimit-*`.

Las decisiones de diseño y los trade-offs están en [DESIGN.md](DESIGN.md).

## Requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download)

## Compilar y testear

```bash
cd rate-limiter
dotnet build
dotnet test
```

## Levantar la API

```bash
dotnet run --project src/RateLimiter.Api
```

Escucha en `http://localhost:5233`.

| Endpoint | Regla | Límite |
|---|---|---|
| `POST /api/login` | `login` (token bucket) | 5 por minuto, ráfaga de hasta 5 |
| `GET /api/orders`, `GET /api/orders/{id}` | `orders` (sliding window counter) | 10 por minuto, compartido entre las dos rutas |
| `GET /api/health` | ninguna | sin límite |

### Probarlo

Con `curl`, 6 logins seguidos: los 5 primeros dan 200 y el 6º da 429.

```bash
for i in 1 2 3 4 5 6; do curl -s -o /dev/null -D - -X POST http://localhost:5233/api/login | grep -iE "^HTTP|x-ratelimit"; done
```

```
HTTP/1.1 200 OK
X-Ratelimit-Limit: 5
X-Ratelimit-Remaining: 4
...
HTTP/1.1 429 Too Many Requests
X-Ratelimit-Limit: 5
X-Ratelimit-Remaining: 0
X-Ratelimit-Retry-After: 12
```

También está [src/RateLimiter.Api/RateLimiter.Api.http](src/RateLimiter.Api/RateLimiter.Api.http), que se ejecuta desde VS Code (extensión REST Client), Visual Studio o Rider.

## Configuración

Las reglas están en [src/RateLimiter.Api/appsettings.json](src/RateLimiter.Api/appsettings.json):

```json
"RateLimiting": {
  "CleanupInterval": "00:01:00",
  "Rules": {
    "login":  { "Algorithm": "TokenBucket",          "Limit": 5,  "Window": "00:01:00" },
    "orders": { "Algorithm": "SlidingWindowCounter", "Limit": 10, "Window": "00:01:00" }
  }
}
```

- `Algorithm`: `TokenBucket` o `SlidingWindowCounter`.
- `Limit`: máximo de requests por ventana. En token bucket es la capacidad del balde.
- `Window`: tamaño de la ventana (`hh:mm:ss`). En token bucket es lo que tarda el balde en llenarse desde vacío.
- `CleanupInterval`: cada cuánto se borran los clientes inactivos.

Un endpoint usa una regla con el attribute `[RateLimit("nombre")]`, en el método o en el controller entero. Si una regla es inválida o un endpoint usa una que no existe, la app **no arranca** y el error dice qué está mal.

Las reglas se leen al arrancar: para cambiarlas hay que reiniciar.

## Logs y métricas

- Cada rechazo se loguea con la regla y la IP. Los errores del limiter se loguean como `Error`.
- Dos contadores, `ratelimiter.allowed` y `ratelimiter.rejected`, etiquetados por regla. Para verlos en vivo:

```bash
dotnet tool install --global dotnet-counters
dotnet-counters monitor -n RateLimiter.Api --counters RateLimiter.Api
```

## Estructura

```
rate-limiter/
├─ src/RateLimiter.Core/     algoritmos, en C# puro (sin HTTP)
├─ src/RateLimiter.Api/      ASP.NET Core: middleware, attribute, reglas, limpieza, controllers de demo
└─ tests/RateLimiter.Tests/  tests de algoritmos, concurrencia, reglas e integración del middleware
```
