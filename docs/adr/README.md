# Architecture Decision Records

Durable decisions with their rationale — the ones that stay expensive to
re-litigate and quietly erode if nobody wrote down *why*. Shorter-lived planning
decisions live as `D<n>` entries in [`../plan.md`](../plan.md); an ADR is
where a decision graduates when it outlives the plan.

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-client-stays-dumb-trust-boundary.md) | No client ever holds AWS credentials — every read and write is brokered by the API. | Accepted |
| [0002](0002-desktop-auto-update-deferred.md) | No desktop auto-update while distribution is hand-held; revisit before any firm depends on a desktop build. | Superseded by [0004](0004-react-native-replaces-flutter.md) |
| [0003](0003-flutter-app-layout.md) | The Flutter app follows Flutter's own architecture guide — UI by feature, data by type, no `lib/src/`. | Superseded by [0005](0005-expo-app-layout.md) |
| [0004](0004-react-native-replaces-flutter.md) | React Native on Expo replaces Flutter everywhere; free tier only, bare primitives, no component library, desktop deferred. Carries the six-round UI spike measurements. | Accepted — *Marketing stays put* revised by [0006](0006-owned-cross-platform-design-system.md) |
| [0005](0005-expo-app-layout.md) | The Expo app follows Expo's own project structure — `src/app/` is routes-only, screen bodies in `src/screens/`. | Accepted |
| [0006](0006-owned-cross-platform-design-system.md) | One owned, platform-split design system serves both surfaces — a shared props module plus `.web`/`.native` leaves per component, the consumer's bundler picking the leaf; the package publishes source. Carries the cutover measurements and the Tamagui reference numbers. | Accepted |
| [0007](0007-hosted-ui-pkce-refresh-token-in-local-storage.md) | The web SPA signs in via the Cognito hosted UI with authorization-code + PKCE; access and ID tokens stay in memory, the refresh token is persisted in `localStorage` — an eyes-open XSS trade-off held down by rotation, revocation and a required CloudFront CSP. The API is called with the access token. | Accepted |
| [0008](0008-testing-shape-follows-the-code-it-tests.md) | Testing shape is per-area, not one house rule: pyramid over the Python services' domain layer, trophy over the UI, contract pins at the API seam, E2E deliberately scarce. Static analysis is the base layer; no coverage gate. | Accepted |

New ones are `NNNN-kebab-title.md`, numbered in sequence, opening with
**Status / Date / Relates to** and then **Decision → Context → Consequences**.
Add the row above. Supersede rather than rewrite: an ADR that turned out wrong
is more useful with its reasoning intact and its status changed.
