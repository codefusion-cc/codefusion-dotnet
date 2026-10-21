# Security

## Reporting a problem

Please report it privately through GitHub: the repository's **Security** tab, then **Report a vulnerability**. Don't
open a public issue. You'll get an answer within a week, and a fixed release as soon as the fix is verified.

## Supported versions

Fixes go into the latest release only.

## CodeFusion.Http

- **Strict parsing.** Requests are parsed as RFC 9112 says, and refused (400) where a lenient parser and a proxy
  could disagree about where a request ends: Content-Length together with Transfer-Encoding, repeated framing
  headers, a bare LF, whitespace before a header's colon, obsolete line folding and malformed chunk sizes. A repeated
  Authorization header is refused too, so a proxy that reads the last one can't see another identity than the app.
- **Limits.** The request line, the headers, their count and the body each have a limit (Kestrel's defaults, and a
  1 MB body). A request's head must arrive within `RequestTimeout`, a new connection's first request within
  `RequestTimeout` of connecting. A body must keep up `MinRequestBodyDataRate`, counting its own bytes and not chunk
  framing, and responses the socket couldn't take at once must be read at `MinResponseDataRate`, counting what the
  client's TCP acknowledged, not what the server's socket buffered (both Kestrel's 240 B/s after a 5 s grace period).
  Each byte pays for 1/rate seconds, but never more than `RequestTimeout` (or the grace period, if longer) past the
  moment it came: bytes sent or read fast at first buy at most that much slack, so a client that then slows below
  the rate is cut off within it, plus what its slow bytes pay for. A client faster than the rate between any two of
  its sends (or acknowledgments) is never cut off by it. A body read that gets no byte for `RequestTimeout` ends the
  connection, and so does a send the client acknowledges nothing more of for that long. So a slow client can't hold
  a connection by sending or reading a byte at a time, whatever it sent or read first, and one that reads nothing is
  cut off at the latest `RequestTimeout` after its own receive buffer filled (TCP can't tell it apart from a slow
  reader before that). Elsewhere than Linux and macOS, what the server's socket took after it first filled counts
  instead. After a refused request the server reads and drops what still comes for 2 s in all, not 2 s per read. A
  stop resets the connections it has to abort.
- **Abandoned work.** A client that closes or resets its connection while its request is handled cancels the
  handler's `HttpExchange.Aborted` (at once, or within a heartbeat when it had sent its next request first; a close
  behind such a request is seen on Linux and macOS only), so a handler that passes the token on stops work nobody
  waits for, behind a proxy too. The cancel's callbacks never run on the heartbeat's thread, so a handler can't stall
  the timeouts above. After a close the answer is still sent if the handler finishes; after a reset it is dropped.
- **Memory.** A request's buffer starts at 4 KB and doubles as its bytes arrive, up to the whole request, so a
  connection holds at most about twice what it has sent, whatever its Content-Length declares. A response is built
  whole in memory and kept until the socket has taken it: a client reading at 240 B/s holds a 1 MB response for up to
  73 minutes. `MaxConnections` caps open connections; past it a new connection is closed at once. There is no cap by
  default, as in Kestrel, so the memory slow clients hold grows with their number: set it, or a proxy's limits, when
  the server faces the internet.
- **Paths** are decoded once, in place, with `%2F` kept escaped and `.` and `..` segments removed, so a handler sees
  the path a proxy would.
- **Tokens** (`AddJwtAuth`, `Jwt`) are HS256 only, signed with a secret of at least 32 bytes, and carry `sub`, `iat`,
  `exp` and the string claims the app declares: no `iss`, `aud` or `jti`, so any app sharing the secret accepts
  them. Nothing revokes one before its `exp` except changing the secret, which ends every session. They live 30 days
  unless `AddJwtAuth(secret, lifetime)` says otherwise. A signature has one spelling: base64url with nonzero spare
  bits is refused, and it is compared in constant time.
- **The scheme** (`JwtOptions.Scheme`, `Token` unless set) is matched byte for byte after the header's surrounding
  spaces and tabs are trimmed, then exactly one space: `bearer`, `Bearer  <jwt>` and `Bearer<jwt>` are refused, not
  read leniently, so the app and a proxy in front of it can't disagree about which token a header carries. A scheme
  must be one RFC 9110 token, checked when the app starts.
- **Claims** beyond `sub`, `iat`, `exp` and `nbf` are read only when the app declares them (`JwtOptions.Claims`,
  string claims only), and only after the signature has been checked in constant time and the header found to be
  HS256: nothing of a forged token is decoded into the arena or parsed. One pass reads the payload. A name that
  doesn't unescape, an `exp` or `nbf` that isn't a number, or JSON that doesn't parse makes the token invalid. A
  `sub` that isn't a positive integer string, or a declared claim that is absent (when required), not a string, not
  valid UTF-8 (raw bytes or a lone surrogate escape), or outside its byte lengths, makes the payload invalid. Every
  copy of a repeated claim is checked, so a bad first copy can't hide behind a good last one. A token is at most 4 KB,
  which bounds a claim. Values are unescaped into the request's arena and are valid only while the request is
  handled, like any `Utf8` of it. Declared names can't be `sub`, `iat`, `exp` or `nbf`, and are compared after
  unescaping, case included.
- **A short secret** is refused unless the app passes `allowShortSecret: true` (`AddJwtAuth(secret, lifetime, true)`
  or the `Jwt` constructor), for a deployment handed a fixed one, such as the Conduit contest's 26 bytes. An empty
  one is refused even then. The tokens are standard HMAC-SHA256 (a key shorter than the 64-byte block is padded with
  zeros, RFC 2104), so they verify anywhere, but every token is an offline test of a guess: a secret short or
  guessable enough to be found lets whoever finds it sign a token for any user, and no rate limit sees the guessing.
  Use it only when the secret isn't yours to choose, and rotate it to 32 random bytes or more when it is.
- **Buffers.** The server's own pooled request and response buffers are reused within the process without being
  wiped, for speed: a request's bytes (a token, a password) stay in memory until a later request overwrites them.
  Arrays that go back to `ArrayPool<byte>.Shared` (buffers grown past their pool's size, the arena's large chunks)
  are cleared first, since other code in the process rents from there. `Viewer.Token` and every `Utf8` of the
  request point into the request buffer and are valid only while that request is handled. The CFH0004 analyzer
  refuses a class that keeps one: in a field, an auto-property, or a primary-constructor parameter one of its members
  uses, and a struct's static field or auto-property. A CodeFusion.Data entity's instance members are exempt, since it
  is never built, but not what a class that isn't an entity inherits from one.
- **Binding.** A body or `[FromQuery]` struct is bound only through what code outside it could set: its public fields
  and its public properties with a public setter. An internal or private member, or one with a non-public setter, is
  never read from the request, so a client can't set a flag such as `IsAdmin` by sending it. An internal field or
  settable auto-property gets warning CFH0109 (make it private or `[JsonIgnore]` to say it isn't data), and a
  required member the binder can't set is error CFH0105. `[Range]` bounds numbers in the body as in the query.
- **No TLS and no HTTP/2.** Put it behind a proxy that terminates TLS when it faces the internet.

## CodeFusion.Data.Postgres

- **SCRAM-SHA-256** with the server's signature checked in constant time, and 4,096 to 1,048,576 iterations (Postgres
  uses 4,096): fewer would make the proof a server posing as Postgres collects cheaper to crack. The password goes
  through SASLprep first, as libpq and the server do.
- **Weak logins only when allowed.** Without TLS, anything posing as the server could ask for the password, so with
  `AllowWeakPasswordAuth` off (the default) the client refuses a cleartext or md5 request, SCRAM with fewer than
  4,096 iterations, and a server that lets a user in without asking for the password that is set (trust), which
  would otherwise get every statement and answer it. Trust works when no password is set.
- **No TLS.** A connection string or URL that asks for it (`sslmode` require or verify, `ssl`, `requiressl`,
  `sslnegotiation=direct`, `sslrootcert=system`, `gssencmode=require`, in libpq's or Npgsql's spelling) is refused,
  and so is a libpq environment variable that does (`PGSSLMODE`, `PGREQUIRESSL`, `PGSSLNEGOTIATION`,
  `PGSSLROOTCERT`, `PGGSSENCMODE`) unless the connection string set the mode itself: never a login in the clear where
  TLS was asked for. Use it on a private network or over a tunnel.
- **Parameters are always bound**, in binary, never spliced into SQL text. Startup parameters are checked too: none
  may hold NUL or set the user, database, client encoding or replication, and a server that switches the client
  encoding from UTF-8 loses the connection.
- **The password** is `PgSettings.Password`, a string: .NET can't wipe it, and it stays in memory as long as the
  settings do, since every new connection logs in with it. The copies the client makes to log in are wiped once used:
  the UTF-8 bytes SCRAM hashes, and on the stack its salted password, client, stored and server keys and the proof
  (sent in the clear, but with the stored key it gives the client key back); the md5 input and its hex hash; the
  cleartext message and its send buffer. Not wiped: what the runtime's PBKDF2, HMAC and MD5 hold while they run, which
  is theirs to clear, and a password that SASLprep must normalize (rare: compatibility characters, combining marks),
  which passes through one more string.

## CodeFusion.Security.Bcrypt

### What it does to keep hashes safe

- **Constant-time check.** The computed hash is compared with the stored one using
  `CryptographicOperations.FixedTimeEquals`; a test times it against an early-exit comparison.
- **No silent truncation.** bcrypt reads at most 72 bytes of a password. A longer password is refused (`Hash`
  throws, `Verify` returns false) instead of being cut, so two passwords that share their first 72 bytes never match
  each other. `EnhancedHash` digests the password with SHA-2 first and takes any length.
- **No lossy text.** A password that isn't valid UTF-16 (a lone surrogate) is refused rather than turned into U+FFFD,
  which would make different passwords hash alike.
- **Strict parsing.** Only `$2a$`, `$2b$`, `$2y$` and `$2$` hashes with a cost of 4 to 31 are accepted. `$2x$`
  (PHP's buggy hashes from before 5.3.7) is refused. A malformed hash never matches and never throws.
- **Wiped buffers.** Key material, the expanded Blowfish state and the queue's pooled requests are zeroed after use.
- **Random salts** come from `RandomNumberGenerator`.

### Things to do in your application

- Check a hash even when the account doesn't exist (against a fixed dummy hash), so response times don't reveal
  which accounts exist.
- Use a cost of at least 10; raise it over time and use `PasswordNeedsRehash` to upgrade hashes at sign-in.
- Check only hashes you made or imported deliberately. A hash's cost decides the work: a cost-31 hash takes days to
  check. When hashes come from elsewhere, read the cost with `InterrogateHash` first and refuse ones above your limit.
- Limit sign-in attempts. With `BcryptQueue`, set `MaxQueued` so a flood of sign-ins is refused quickly instead of
  queueing minutes of work.
