# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Stack

- **Engine**: Unity 6.3 (6000.3.16f1)
- **Render Pipeline**: URP (Universal Render Pipeline) v17.3.0
- **Language**: C#
- **Input**: Unity Input System (`InputSystem_Actions`) — never use the legacy `Input` class
- **Camera**: orbit rig — a `CameraRoot` pivot rotated by mouse in `LateUpdate`, followed by a Cinemachine virtual camera
- **Movement**: `CharacterController` — not `Rigidbody` (projectiles use `Rigidbody`)

## Architecture

### Core Principles

1. **Data-driven**: Game data lives in ScriptableObjects, not hardcoded in MonoBehaviours.
2. **Module isolation**: Each domain is a separate asmdef. Respect the explicit dependency table below; use read-only contracts/services across allowed dependencies and the EventBus in `Game.Core` for global notifications. Never bypass another module's authoritative state.
3. **Unidirectional dependencies**: Core never depends on upper layers.

### Assembly Hierarchy

| Assembly | First-party dependencies |
|---|---|
| Game.Core | None |
| Game.Materials | None |
| Game.NetworkProtocol | None; pure shared contracts from the sibling Shared/Protocol local UPM package; Google.Protobuf is third-party |
| Game.Networking | Core, NetworkProtocol; Mirror / Mirror.Transports / kcp2k; transport and bounded codecs only |
| Game.Multiplayer | Core, NetworkProtocol, Networking, Character, Combat; N3 authority/prediction/replica composition; Character remains network-agnostic |
| Game.Combat | Core, Materials |
| Game.Skills | Core, Combat |
| Game.Run | Core, Combat, Skills |
| Game.Character | Core, Combat, Skills, Run |
| Game.ElementField | Core, Combat, Materials |
| Game.Rendering | Core, Combat, ElementField (read-only), Materials |
| Game.UI | Core, Combat, Skills, Run (requests/read-only services) |

Editor tooling belongs to **Game.Project.Editor**, never a development-task-named Assembly. The current navigation and full player flow are documented in `Assets/_Project/Docs/项目架构与目录导览.md`.

New code goes in the lowest assembly that satisfies its dependencies. Cross-module communication goes through EventBus events in `Game.Core`. `Game.ElementField` may depend on Combat status/projectile contracts, but `Game.Combat` never depends back on ElementField. Presentation (e.g. UI or `Game.Rendering`) reacts to gameplay through events or read-only interfaces — gameplay never references presentation.

### Folder Conventions

| Path | Purpose |
|------|---------|
| `Assets/_Project/Scripts/<Assembly>/` | C# source, one subfolder per assembly |
| `Assets/_Project/Prefabs/` & `Assets/_Project/Art/Prefabs/` | Prefabs |
| `Assets/_Project/ScriptableObjects/` | Runtime data (SO instances) |
| `Assets/_Project/Art/` | Textures, models, audio, Animator Controllers |
| `Assets/_Project/Scenes/` | Unity scene files |
| `Assets/ThirdParty/` | Third-party packages not via Package Manager |
| `docs/superpowers/specs/` & `docs/superpowers/plans/` | Feature design specs and implementation plans |
| `Assets/_Project/Docs/` | In-depth design analyses (e.g. `M3_Combat_Design.md`, `Dash_System_Design.md`) |

Production assets are grouped by responsibility, never by development phase/version. Spells, spell items, wand stats/items and programs live under `ScriptableObjects/Spell/{Definitions,Items,Wands,Programs,Libraries}`; only old GUID compatibility copies live in `Spell/Compatibility`. Other items use `ScriptableObjects/Items`, world/station/raid configuration uses `ScriptableObjects/World`, input uses `ScriptableObjects/Input`. Test-only content lives in `Tests/Fixtures`; Review scenes belong in `Scenes/Tests`, never beside the formal Hub/Home. Materials belong under `Art/Materials`, not ScriptableObjects. Preserve ContentIds, serialized field names and scene-load paths when reorganizing; existing `norn.v2.*` IDs remain save contracts, not a reason to recreate version directories.

## Online foundation (N0–N1)

- Keep sibling workspace folders `RPG / Server / Shared` under the outer Monorepo Git root. Clone the complete repository and Git LFS; do not initialize nested component repositories. Unity and Shared relative references remain unchanged.
- `Shared/Protocol` is a `netstandard2.1` Class Library plus local UPM package. Schema lives in `Protos`, unique generated C# in `Runtime/Generated`; do not import a duplicate first-party protocol DLL.
- Use the project-local .NET SDK 10.0.402 / runtime 10.0.13 through `Tools/Networking/Invoke-OnlineDotNet.ps1`; keep system SDK installations unchanged. Google.Protobuf 3.34.2 / protoc 34.2 are pinned in dependency-lock.json.
- `Test-OnlineFoundation.ps1` covers foundation HTTP/Protobuf only; KCP, Dedicated Server, database and multiplayer/Profiler acceptance remain separate gates. Update the Chinese living online explanation after each Task.
- Mirror 96.11.3 is now imported with original metadata under ThirdParty/Networking/Mirror, excluding Examples/Tests. N0-2 is a loopback-only transport lab, without business authentication/gameplay replication. Use explicit Online menus for scene setup and Client/Server builds; preserve formal scenes and global build scene settings. Pooled messages require Schema-aligned partial reset including unknown fields.
- N0-2 visible GUI acceptance was delegated to Codex and passed on 2026-10-21; evidence is under `docs/superpowers/evidence/multiplayer/n0-2-gui-startup`. Do not request that same gate again. N0 local acceptance subsequently passed; see `docs/superpowers/evidence/multiplayer/n0-final/acceptance.json`. Lab Server builds add `APP_UI_EDITOR_ONLY` per build, and PortalTransitionFade startup is Editor/Client only. Two native Built-in Resource startup errors remain unresolved; formal Gameplay/remote compatibility must track them and must not assume harmless fallback.
- N0-4 TransportLoadLab is a separate four-client loopback numeric/text experiment, without authentication/gameplay. FunctionalPassed and ZeroAllocationPassed are independent gates; maintain the two existing Chinese online learning documents.
- A known 1 KiB control allocation returned 0 from GC.GetAllocatedBytesForCurrentThread in the current Unity runtime. Do not use those byte zeros (including old World/Grid Tick diagnostics) as allocation evidence. Network scope measurements use a calibrated fixed-capacity current-thread GC.Alloc event recorder; event counts are not bytes. Keep full-frame ProfilerRecorder results separate and calibrate measurement tools before claiming zero.
- Final local N0: 67 EditMode / 10 business tests; four-client numeric, deterministic 5% loss and bounded development-auth snapshots passed steady zero allocation. Text positive control allocates. Development credentials/ActorIds are not accounts or JoinTickets. List-fingerprint EncryptionTransport function/rejection passed but steady allocations remain; remote gate is false, N8 must remediate. MySQL/accounts/formal gameplay are not implemented.
- Raw IO adapter supports Windows IPv4 loopback only; Linux/public network/IL2CPP remain unvalidated. Preserve four Mirror patches plus hashes; full allocation traces use diagnostic Burst=64, normal acceptance uses 8. Keep traces distinct from budget evidence.
- Normal lab builds preserve original Rendering/Resources inputs. Rendering scope reloads RP assets by GUID/path and restores original settings bytes. Do not reuse managed references after their native Assets were unloaded. URP Global Settings removal was reverted after RenderGraphSettings rejection; isolation menus are diagnostics only. Preserve formal scenes/global Build Scene List.

### Current N1 entry and acceptance

- User ruling2026-10-22: main-menu Login/Register is mandatory even for single-player. Mode choice is F at dungeon entry portals, never a main-menu Online Mode button; return/extraction portals retain their duties.
- `OnlineAccountBootstrap` owns the separate persistent application Session root; UI unload does not log out. EnterHub requires a fresh successful business Profile response, not just a cached Token. Tokens remain memory-only; online/local assets stay isolated.
- `DungeonEntryChoiceGate` owns pending choice, actor/range validation and its Pause Reason. UI only sends Core intents. N2 now connects F-portal matching; unknown outcomes must never silently fall back to single-player.
- N1 local evidence: n1-final/acceptance.json; 26 business/MySQL,83 related EditMode,3 full-flow PlayMode, native D3D11 persistence/idle0B. N0 encryption/native startup errors, D3D12 Client, Linux/public/IL2CPP remain separate unpassed gates.
- Private MySQL8.4.13:127.0.0.1:13384,dev/test Schema,least-privilege app/migrate/control users; --no-monitor and PID/start-time/path identity checks plus authenticated SHUTDOWN. Never operate the existing system instance or commit local secrets. Keep fundamentals/GUI instructions separate from implementation history.

### Current N2 instance/admission boundary

- N2 local acceptance is complete2026-10-23: n2-final/acceptance.json and cleanup.json,58 real-MySQL business/91 related EditMode/6 full-flow PlayMode plus two final visible native Clients. Wait for user N2 confirmation before N3. OnlineRoom is only an authenticated connection staging scene, no actors/combat.
- Normal and diagnostic local business menus now enable Rooms/Instances; deployment defaults remain false. Same-Dungeon atomic matching,2–4 seats,ReadyRevision/current asset version,leader Start/DS Ready/Ticket; Open-lobby presence90s never expires Starting/Running. Ticket budget persists5s minimum and one row per member including consumed tickets.
- Preserve/deactivate Hub roots and load OnlineRoom additively after authentication; return restores original Hub/session. Re-entry same Actor/higher Epoch; check connectionId before sequence/rate state. Unknown cancellation must be confirmed; expired Open-lobby identity restores local input without client asset unlock.
- Formal Room uses RoomProtocolEnvelope ID41798; legacy ProtobufEnvelope ID44385 collides with Mirror.ObjectDestroyMessage and stays N0-lab-only. Keep JoinAdmissionTests ID checks; do not hide collisions via ReplaceHandler.
- Preserve migrations001–003;004 adds queue/presence/ticket budget. One-use Ticket60s and ConnectionEpoch fencing are separate from player Session. DS secret goes through environment with business DB settings removed. Host stop leaves DS alive for PID/UTC StartTicks/normalized-path re-adoption; only kill matching owned processes.
- Ready45s,heartbeat15s,freeze60s,lease120s,idle shutdown180s; CanSimulate requires business Ready. Final evidence n2-final, living Task N2-2B/foundation28; previous n2-1/n2-2 remain historical. Windows loopback only, no public/Linux/IL2CPP/D3D12/encrypted-zero-GC acceptance.
- RoomManager+Transport useGUILayout=false prevents inherited OnGUI layout736B/frame; formal UGUI stays enabled. Serialized DualMode=false before Awake is required for current Windows IPv4 Raw IO. Actual final NativeSocket=true/calibrated120-frame Client connected windows peak0B; excludes lobby HTTP/diagnostic IO/Server heartbeat. Keep failed native-before-layout-fix/callstack evidence distinct from final green results.

## Code Conventions

| Category | Style | Example |
|----------|-------|---------|
| Private / protected fields | `_camelCase` | `_moveSpeed` |
| Public members & types | `PascalCase` | `MoveSpeed`, `DamageSystem` |
| Interfaces | `IXxx` | `IDamageable`, `IHealable` |
| Namespaces | `Game.<Assembly>` | `Game.Combat`, `Game.Core` |

Every script must declare a namespace matching its assembly.

### Learning-oriented comments and setup instructions

- New scripts and substantive edits need concise Simplified Chinese comments explaining responsibilities, architecture boundaries, non-obvious control flow and design reasons.
- `.proto` files need compact responsibility sections, summaries above message/enum definitions, and Chinese comments for fields/enum values on the same line after the semicolon, with aligned columns where practical, matching the user's MyMMO reference. Explain business meaning, relevant units/defaults and compatibility; put long conceptual explanations in the separate foundation guide. Update generated comments through the Schema and regeneration, never by editing generated C#.
- Teach setup through IDE/Editor operations, official package sources and concrete file settings. Do not use PowerShell or terminal recipes as the learning workflow unless explicitly requested; explain what to click, why it is needed and what confirms success.
- Maintain the separate beginner foundation guide `Assets/_Project/Docs/2026-10-21 双端开发基础指南（是什么、为什么、怎么操作）.md` for what/why/manual operations; retain implementation history and evidence in the living online explainer.

## Performance Rules

The following are **forbidden** inside `Update()`, `FixedUpdate()`, or any per-frame hot path:

- `new` allocations (objects, arrays, closures, delegates)
- LINQ queries (`Where`, `Select`, `FirstOrDefault`, etc.)
- Boxing (casting value types to `object` or a non-generic interface)

State objects are pre-instantiated in `Awake()` and reused — never allocate them at runtime. One-shot allocations on discrete input (e.g. `Instantiate` an arrow on release) are fine — only per-frame allocation is the concern. Verify zero GC Alloc in the Unity Profiler before considering a feature complete.

## Workflow

- **Before any cross-module or architectural change**: write a plan and confirm alignment first.
- **Subagents**: use SubAgents when independent investigation, implementation or review benefits from parallel work. Assign clear file ownership; coordinate shared Unity Editor, builds, tests and Git mutations in the main agent. The user removed the previous prohibition on 2026-10-23.
- **Feature flow**: non-trivial features normally go brainstorm → one detailed implementation plan → user confirmation → execute. Save the Chinese plan under `docs/superpowers/plans/`; do not create a separate spec unless the user explicitly requests one. Large features ship in **phases**, each gated by the developer verifying in the Editor before the next begins.
- **Incremental learning document**: for P2 and subsequent P3/P4/P5 development, maintain one Simplified Chinese living explainer under `Assets/_Project/Docs/` for the whole phase. Update it after every completed Task with fundamentals, implementation technology, architecture integration, alternatives/trade-offs, tests, debugging, and interview-oriented explanations. Start every Task with a several-hundred-Chinese-character overview explaining its purpose and completed work; restart section numbering from 1 inside every Task; prefer a few information-dense sections over many shallow numbered headings. Explicitly list every created/modified script and explain its responsibility, assembly/architecture position, integration path, relevant C#/Unity technology or built-in APIs, and important trade-offs. The document remains explicitly "in progress" until the entire phase is complete.
- **Renaming a MonoBehaviour script**: move the `.cs` **and its `.meta` together** (`git mv` both) so the script GUID survives and prefab/scene references don't break — never delete-and-recreate (that orphans the reference and resets serialized fields).
- **每轮汇报**：每轮结束明确列出本轮完成内容、实际验证及限制、接下来具体推进项。
- **Commits**: use [Conventional Commits](https://www.conventionalcommits.org/). Examples:
  - `feat(combat): add hit-stun state machine`
  - `fix(core): prevent EventBus null-ref on first-frame subscription`
  - `refactor(character): split StatBlock into separate ScriptableObject`

---

## Core Infrastructure

### GameLog — Debug Logging

`Game.Core.GameLog` is the global logging entry point. **Never call `Debug.Log` directly.**

```csharp
GameLog.Info("message");
GameLog.Warn("message", "Combat");    // outputs [Combat] message
GameLog.Error("message", "EventBus");
```

All three methods are marked `[Conditional("DEVELOPMENT_BUILD")]` + `[Conditional("UNITY_EDITOR")]`. In Release builds the entire call site (including string interpolation and boxing) is stripped at the IL level — zero GC Alloc.

### EventBus — Cross-Module Communication

`Game.Core.EventBus<T>` is the only channel for cross-module events.

**Define an event** (must be `struct` + `IGameEvent` — never `class`):
```csharp
public struct PlayerDiedEvent : IGameEvent
{
    public Vector3 Position;
}
```

**Subscribe / Unsubscribe** (standard MonoBehaviour pattern):
```csharp
void OnEnable()  => EventBus<PlayerDiedEvent>.Subscribe(OnPlayerDied);
void OnDisable() => EventBus<PlayerDiedEvent>.Unsubscribe(OnPlayerDied);

void OnPlayerDied(PlayerDiedEvent e) { ... }
```

**Publish**:
```csharp
EventBus<PlayerDiedEvent>.Publish(new PlayerDiedEvent { Position = transform.position });
```

**Scene cleanup** (prevent stale subscriber leaks):
```csharp
EventBus<PlayerDiedEvent>.Clear(); // call for every event type used in the scene
```

`Publish` is zero-GC on the hot path: `T` is constrained to `struct`, so JIT passes by value with no boxing.

---

## Character System

`Game.Character` drives both players and enemies, each with its own hand-rolled state machine (`PlayerStateMachine` / `EnemyStateMachine`). Two architectural constraints:

- **States are plain C# objects**, not MonoBehaviours — pre-instantiated in `Awake()` and driven manually (Unity never calls their `Update`). `PlayerStateMachine.ChangeState` runs `Exit()` → swaps reference → `Enter()`, **unconditionally** — so `Exit()` is the single guaranteed place to release per-state resources (start dash cooldown, close a hit window).
- **`CharacterController.Move()` runs in `Update()`**, never `FixedUpdate()` (Unity constraint). The camera pivot rotates in `LateUpdate()`, after all moves.

### Multi-character architecture (base + subclass)

Three playable archetypes share locomotion but **not animations** — each has its own Animator Controller and clips:

```
PlayerControllerBase (abstract MonoBehaviour)   ← move / jump / dash / camera / state machine + Grounded/Airborne/Sliding/Dash
├── WarriorController   — melee: MeleeHitDetector, ComboDefinition, BladeTrail, PlayerAttackState
├── ArcherController    — ranged: arrow prefab/spawn, ComboDefinition, ChargeAttackDefinition, PlayerBowAttackState + PlayerChargeAttackState
└── WizardController    — ranged caster: left click runs the current WandLoadout through SpellCaster/CastEvaluator; meteor is ordinary spell data
```

- **Future polymorph contract**: `WarriorController` and `ArcherController` are intentionally retained player-control implementations. A future transformation spell will temporarily turn the Wizard player into these weaker forms, so cleanup work must not remove or weaken their controllers, states, attack data, Animator integration, or prefabs merely because the current main avatar is Wizard.

- **`PlayerControllerBase`** owns everything shared (components, input timers, state machine, the four shared states, the screen-center `ResolveAimTargetPoint` aim helper) and exposes data to states via properties. Subclasses add only attack specifics.
- **Attack seams (two)**: the shared states never name a concrete attack state. `PlayerGroundedState` calls virtual `bool TryStartAttack()` (ground); `PlayerAirborneState` calls virtual `bool TryStartAirAttack()` (air, before the landing check). Base returns `false`; `WarriorController` enters its combo, `ArcherController` routes tap→normal / hold→charge, and `WizardController` consumes a queued cast to run the current `WandLoadout`. This keeps the `Dash → Attack → Jump` priority in one shared place while attack semantics vary per character.
- **State typing**: `PlayerStateBase._player` is typed `PlayerControllerBase`; shared states use only base members. Character-specific states (`PlayerAttackState`, `PlayerBowAttackState`, `PlayerChargeAttackState`, `PlayerWizardAttackState`) take the concrete controller in their constructor and stash a typed field (`_warrior` / `_archer` / `_wizard`) for subclass-only members. **No generics.**
- **`Awake` is `protected virtual`** (template method): the subclass override calls `base.Awake()`, then builds its attack state(s) and pre-hashes its animation state names.

### Files (`Game.Character`)

| File | Purpose |
|------|---------|
| `PlayerControllerBase.cs` | Abstract base: input, timers, camera, state machine, shared states, `TryStartAttack()` hook, data-driven `_dashStateName` |
| `WarriorController.cs` | Melee subclass (combo attack + blade trail). **Was renamed from `PlayerController.cs` keeping its `.meta`/GUID** |
| `ArcherController.cs` | Ranged subclass: normal/charge attack input routing, arrow spawn refs, aim mask |
| `GroundChecker.cs` | `SphereCast` ground detection; `IsGrounded`, `GroundNormal`, `GroundAngle` |
| `PlayerStateMachine.cs` | Minimal FSM: `Exit()` → swap → `Enter()` |
| `States/PlayerStateBase.cs` | Abstract state base; `_player` (`PlayerControllerBase`) + shared `HandleRotation()` |
| `States/PlayerGroundedState.cs` | Ground move, slope, and the transition priority chain |
| `States/PlayerAirborneState.cs` | Split-phase gravity, coyote time, jump-buffer consumption, `TryStartAirAttack()` hook |
| `States/PlayerSlidingState.cs` | Constant-speed slide down steep slope |
| `States/PlayerDashState.cs` | Locked-direction dash via CrossFade; `Exit()` starts the cooldown |
| `States/PlayerAttackState.cs` | Warrior melee combo: per-segment hit + blade-trail windows, `ComboResolver`-driven |
| `States/PlayerBowAttackState.cs` | Archer normal shot: 1-segment combo, spawns an `Arrow` at `ArrowSpawnTime` |
| `States/PlayerChargeAttackState.cs` | Archer charge heavy: draw→hold→release, charge-scaled straight aimed shot, crosshair events |
| `Controllers/WizardController.cs` | Caster subclass: controller-level `UpdateAttackInput()` captures press-instant `ClickAimPoint`, queues a cast through cooldown, then enters `PlayerWizardAttackState` to run the current `WandLoadout` via `SpellCaster`; it contains no hardcoded meteor branch |
| `Spells/SpellCaster.cs` | Bridge from pure spell evaluation to Unity runtime: evaluates a `WandLoadout`, instantiates projectile prefabs, applies damage/speed/spread snapshots, and handles trigger payload recursion on projectile impact |
| `States/PlayerWizardAttackState.cs` | Wizard cast state (ground + air): uses combo segment 0 for animation/timing, then calls `SpellCaster.CastWand(...)` at `ArrowSpawnTime` |

### Animator integration (read before touching combat/dash animations)

- **Continuous params** (`speed`, `isGrounded`) are synced every frame in `PlayerControllerBase.Update()`. **Event triggers** (`jump`) are fired once by the initiating state — never synced per-frame.
- **Code-driven entry**: attack/dash/charge states are entered with `Animator.CrossFadeInFixedTime(stateHash, …)` — code names the target state directly, so **no incoming transition line is needed** in the Controller. The target **state-name string is data-driven** and pre-hashed once via `Animator.StringToHash` in `Awake` (never per-frame): `_dashStateName` (base), `AttackDefinition.AnimationStateName` (per combo segment), and the draw/release names in `ChargeAttackDefinition`. A wrong/empty name fails **silently** (CrossFade to a nonexistent state = no anim change) — empty names log `GameLog.Warn`; a wrong name is a common bug when adding a new character.
- **Gotcha — CrossFade-entered states still need EXIT transitions.** Entry is code-driven, but *leaving* is not: when a dash/attack ends, the code only swaps the FSM state — it does **not** CrossFade back. Each such Animator state must carry its own outgoing transitions (e.g. `Dash_Bow → Idle` with Has Exit Time) or the character freezes in that pose. The per-character Controllers (`SingleTwoHandSwordHero`, `BowHero`) wire these; C# only data-drives the *entry* name. Pure locomotion states (Idle/Run/Jump*) are driven the normal way — by `speed`/`isGrounded`/`jump` transitions in the Controller.

### State transitions (shared)

```
Grounded ──dash buffered & off cooldown────────► Dash       (highest priority)
Grounded ──TryStartAttack() (per character)────► Attack / Bow / Charge state
Grounded ──jump / leave ground─────────────────► Airborne
Grounded ──steep slope─────────────────────────► Sliding
Airborne ──land (normal / steep slope)─────────► Grounded / Sliding
Sliding  ──lost ground / slope normalizes──────► Airborne / Grounded
Dash     ──duration elapsed (Exit→cooldown)────► Grounded / Airborne
Attack*  ──anim ≥ ~85%──────────────────────────► Grounded / Airborne
```

### Input: buffers and the timer pattern

A uniform **buffer-counter pattern** underlies all forgiveness windows: an input callback sets `Counter = <time>`; `PlayerControllerBase.Update()` decrements every counter each frame; the owning state treats `> 0` as "pending" and zeroes it on consume. Applied to Jump Buffer, Coyote Time, Attack Buffer, **Dash Buffer + Cooldown** (two orthogonal counters: buffer = sub-frame forgiveness, cooldown = ability lock), and the combo input window.

- **`PlayerGroundedState.CheckTransition` order = priority**: Dash → Attack → Jump → leave-ground → slope.
- **Tap/hold vs cast queue**: `ArcherController.TryStartAttack` still polls `IsAttackHeld` and accumulates hold time — past `TapThreshold` → charge state; released earlier → normal shot. `WizardController` no longer does tap/hold semantics: its always-running `UpdateAttackInput()` captures the crosshair aim (`ClickAimPoint`) on the press edge and queues one cast. The queue survives attack animation/cooldown, so fast clicks fire reliably toward the press-instant aim instead of being eaten by cooldown or drifting with later camera movement.
- **Pause guard**: `PlayerControllerBase.Update()` / `LateUpdate()` return early when `Time.timeScale == 0f`, so the wand editor can pause gameplay, unlock the cursor, and prevent camera/input leakage while UI drag-drop is active.
- Jump feel: **Coyote Time** (jump shortly after leaving ground), **Jump Buffer** (jump pressed airborne fires on landing), **split-phase gravity** (`FallGravityMultiplier` > `GravityMultiplier`).

### Enemy AI (`Game.Character/Enemy`)

Enemies mirror the player's hand-rolled FSM but with a **separate** machine and base class — `EnemyStateMachine` + `EnemyStateBase`, driven by the abstract `EnemyControllerBase`. Two archetypes subclass it:

- `MeleeEnemyController` — chases into melee range, `EnemyAttackState` (OverlapBox via `MeleeHitDetector`).
- `RangedEnemyController` — kites within a range band, `EnemyRangedAttackState` (spawns a `Fireball`).

All tunables are data-driven via `EnemyDefinition` (SO): move speed, detection/lose radii (hysteresis), attack range/cooldown, hurt-stun duration, ranged retreat band + projectile. `EnemyPerception` does radius-based player detection (the player registers itself in `PlayerControllerBase.Current`, so no per-frame `FindObjectOfType`). States: `Idle / Chase / Attack / RangedAttack / Kite / Hurt`. HP/team stay on `HealthComponent` (not duplicated in the SO). The ranged-enemy path is also the retained foundation for a future ordinary archer enemy; cleanup must not remove it because current encounters mainly use mage/melee enemies.

---

## Combat System

`Game.Combat` is data-driven and shared by all characters (players and enemies). Damage reaches a target through one funnel — `IDamageable.ReceiveHit(in DamageRequest)` — from four sources: melee OverlapBox windows, flying projectiles (`ProjectileBase` subclasses), radius bursts/fields (`AreaDamage`), and status damage-over-time (`StatusController`). Design doc: `Assets/_Project/Docs/M3_Combat_Design.md`.

The damage flow is deliberately split so the math is testable without Unity:

```
MeleeHitDetector (OverlapBox, per-frame while window open)  ─┐
ProjectileBase   (OnCollisionEnter in flight)              ─┤
AreaDamage       (OverlapSphere, one-shot or ticking)      ─┼─► DamageRequest (value snapshot) ─► target.ReceiveHit()
StatusController (DoT tick on the target)                  ─┘     └─ HealthComponent.ReceiveHit()
                                                                       ├─ DamagePipeline.Resolve(req)  ← pure, no MonoBehaviour
                                                                       ├─ subtract HP (clamped ≥ 0)
                                                                       ├─ Publish DamageReceivedEvent  (same frame)
                                                                       └─ Publish DeathEvent           (same frame, if HP ≤ 0)
```

### Animation-driven timing (no Animation Events)

**All combat timing reads `GetCurrentAnimatorStateInfo(0).normalizedTime`** against values in the SO — there is deliberately no parallel Animation-Event mechanism. The same idea covers: melee hit window (`HitActiveStart/End`), blade-trail window (`TrailActiveStart/End`), combo-input window (`ComboInputStart/End`), and the single-point arrow spawn (`ArrowSpawnTime`). **Single-point** triggers use a `bool` "already fired this play" guard (the projectile analogue of `MeleeHitDetector`'s `HashSet` per-swing dedup), and check the current state's `shortNameHash` to avoid mis-firing during a CrossFade transition.

### Files (`Game.Combat`)

| File | Purpose |
|------|---------|
| `IDamageable.cs` | Contract for "anything hittable": `TeamId`, `IsAlive`, `ReceiveHit(in DamageRequest)` |
| `HealthComponent.cs` | `MonoBehaviour` + `IDamageable`. Owns HP/team/invulnerability, resolves & applies damage, publishes events |
| `DamagePipeline.cs` | `static` pure `Resolve(in DamageRequest) → DamageResult`. Unit-tested |
| `DamageRequest.cs` / `DamageResult.cs` | `readonly struct` value snapshots (request in, result out) |
| `DamageType.cs` / `DefenseProfile.cs` | `enum : byte` (`Physical`/`Magical`/`True`); defense struct (mitigation **reserved**, passthrough) |
| `AttackDefinition.cs` | `ScriptableObject` for one attack/segment: base amount, type, `HalfExtents`, the normalized windows above, `ArrowSpawnTime`, `AnimationStateName` |
| `ComboDefinition.cs` | `ScriptableObject` — ordered `AttackDefinition[]` segments (the chain); a 1-segment combo = a single attack |
| `ComboResolver.cs` | `static` pure function → `ComboDecision` (Continue / Advance / End) from index/count, anim progress, buffered input + window |
| `MeleeHitDetector.cs` | Weapon `MonoBehaviour`. While window open, `OverlapBoxNonAlloc` each frame, filters self/ally/dead/already-hit, submits `DamageRequest` |
| `Projectiles/ProjectileBase.cs` | Abstract projectile: `Rigidbody` flight, `Init(...)` snapshots damage/team/velocity/gravity, `OnCollisionEnter` → same-team pass-through / enemy `ReceiveHit` / destroy; virtual `OnImpact` hook plus `Impacted(hitPoint, hitDir)` notification for spell triggers. **Static registry makes same-team projectiles `IgnoreCollision` each other** (no self-deflection on rapid fire; only cross-team projectiles collide & explode). Ignores the caster collider |
| `Projectiles/Arrow.cs`, `Fireball.cs`, `NovaFireball.cs` | `ProjectileBase` subclasses: arrow (parabolic, faces velocity), fireball (straight, explosion + applies `StatusKind.Burning` through `StatusController`), meteor projectile (sky→ground) |
| `Status/AreaDamage.cs` | Reusable radius damage on any VFX prefab: `Init(attackerId, team)`, `OverlapSphereNonAlloc` (zero-GC), one-shot (`TickInterval ≤ 0`, explosion) or ticking (field). Team-filtered |
| `Status/StatusController.cs` | Unified status runtime owner for Burning/Wet/Poisoned/Sticky: intensity, decay/hold, DoT, reactions, VFX, movement multiplier and events |
| `Status/CharacterCombatFeedback.cs` | Hit feedback (flash etc.) by consuming `DamageReceivedEvent` |
| `ChargeAttackDefinition.cs` | `ScriptableObject` — charge tunables: code-entered draw/release state names, tap threshold, max charge time, min/max damage & speed (linear by ratio), `ArrowSpawnTime`, `AimMaxDistance`; maintain remains an Animator transition |
| `EnemyDefinition.cs` | `ScriptableObject` — enemy move/perception/attack/hurt/ranged tunables + an `AttackDefinition` |
| `CombatDamage.cs` | **Reserved seam** — future `Game.Skills` direct-damage entry (`Deal(in DamageRequest, IDamageable)`). Not wired |
| `Events/DamageReceivedEvent.cs`, `Events/DeathEvent.cs` | `IGameEvent` published every hit / on death |
| `_Debug/CombatDebugLogger.cs` | Debug-scene event logger; retained because `SampleScene` still references it |

### Key Decisions

- **Value-snapshot damage**: `DamageRequest` captures attacker data by value, so resolution never re-queries a (possibly destroyed) attacker.
- **Pure pipeline / pure resolver**: `DamagePipeline` and `ComboResolver` are `static`, Unity-free, unit-testable. Mitigation is stubbed passthrough; `DefenseProfile` is the future Buff/Debuff hook.
- **Zero-GC hit detection**: `OverlapBoxNonAlloc` into a pre-allocated `Collider[]` (`MaxHitsPerFrame = 16`) + reused `HashSet<int>` per-swing dedup.
- **Same-frame events**: damage/death events publish synchronously inside `ReceiveHit`, so presentation reacts the same frame (e.g. `CrosshairUI`, debug logger).
- **Team filtering**: targets whose `TeamId == attackerTeam` are skipped (prevents self/friendly hits). For melee, `MeleeHitDetector._attackerTeam` **must match** the owner's `HealthComponent.TeamId` (keep in sync in the Inspector); projectiles and `AreaDamage` read team from the shooter's `HealthComponent` via `Init`.
- **Projectile self-collision**: a static registry of live `ProjectileBase` instances `Physics.IgnoreCollision`s same-team pairs at spawn — rapid-fire shots don't deflect each other and same-team projectiles never collide; only cross-team projectiles collide (and both explode).
- **Projectile hit notification seam**: `ProjectileBase.Impacted` is a generic hit event (point + direction) raised after `OnImpact` and before destroy. It exists so `Game.Character.SpellCaster` can run trigger payloads without `Game.Combat` depending on `Game.Skills`/`Game.Character`.

### Tests

EditMode tests live in `Assets/_Project/Tests/Combat/` (asmdef `Game.Combat.Tests`, NUnit). `DamagePipelineTests` covers the pure damage pipeline and `ComboResolverTests` covers combo decisions. Run in the Unity Test Runner (EditMode).

---

## Current Spell Programming System

The formal player uses one integrated chain: `WizardController` input → `PlayerWizardAttackState` at `AttackDefinition.ArrowSpawnTime` → owned wand execution in `Game.Run` → `SpellProgramCompiler` / `SpellProgramMachine` → frozen graph/commands → `SpellCaster` → projectile lifecycle → normal combat damage funnel. `Game.Run` owns real inventory, mana, eligibility, versions, reservations and layer commits; the pure Skills interpreter does not grant resources. Research UI predicts/replays this same chain and edits authoritative item instances through requests; the backpack remains the compact editor. The research UI is available only inside allowed Hub/Home areas.

`SpellProgramMachine` maintains Deck/Hand/Discard, bounded Draw/Copy execution and independent trigger scopes. Final modifiers affect every output in their own scope, including earlier output cards. Trigger payloads are frozen when building the root and released at the declared lifecycle event; do not reinterpret a mutated SO at impact. Retain NPC/independent program semantics and all Warrior/Archer paths. See the current architecture guide before changing the interpreter.

## NPC / Independent Spell Programs (`CastEvaluator`)

`CastEvaluator` is retained for NPC/Boss and explicitly authored read-only `WandLoadout` programs. `SpellCaster.CastWand` reads its Inspector template for this path. It does not create player ownership, starting cards, or another editable player session. The stateful player execution remains the chain above.

The pure evaluator emits `EmitCommand` snapshots; `SpellCaster` is the Unity bridge, and `ProjectileBase` owns the projectile lifecycle. Preserve the existing bounded trigger semantics, mana policy and enemy/Boss behavior when maintaining this path. Tests remain in `Tests/Skills` and `Tests/Character`.

### Hub-centered player ownership

Main menu starts at `SinglePlayer_Hub`. `SinglePlayerInventorySession` / `ItemOwnershipStore` own actual item instances. `HubPracticeSession` transfers this inventory to P7/P8, returns it to Hub, and captures the real Boss retry checkpoint. The Editor-only direct-scene preview uses an in-memory repository and the same inventory path, never a production save.

The obsolete `RunSpellSession`, `RunSessionCoordinator`, P7 starting library/wand, Palette/Frame editor, duplicate unreferenced compatibility spell assets, and the exit branch that auto-unlocked all spells have been retired. The ordinary NPC evaluator is not part of that retirement. `DungeonFinalRewardPickup` grants finite random rewards into real inventory; a full inventory retains pending rewards. Exits return to Hub without granting the full library.

`Game.UI` has a compact inventory editor and a separate `WandResearchController` / `WandProgramWorkbenchPresenter` for allowed Hub/Home regions. The workbench renders the same execution graph as rows of spell cells, with highlighting, pan and zoom; do not reintroduce the old Palette/Frame editor. Navigation out of a carried-inventory scene must save first; on failure keep the scene and assets intact.

Resource filenames use underscores and PascalCase: `Spell_Emit_Fireball`, `Spell_Modify_DamageUp`, `Spell_Multicast_DoubleShot`, `Spell_Copy_Alpha`, `Item_Spell_Emit_Fireball`, `Wand_ResearchRapid`. Stable ContentIds such as `norn.v2.*` remain unchanged for save compatibility. Do not use them as filenames or create version/stage directories.

---

## Presentation (`Game.Rendering` / `Game.UI`)

Presentation is **read-only on gameplay**: it never references `Game.Character`; its only `Game.Combat` dependency is gameplay data/events consumed for presentation. It reacts through EventBus and never modifies gameplay state.

- **`Game.Rendering`**: `CrosshairUI` (shows/positions the aim reticle), `AimFramingController` (camera framing), and `StatusMaterialController` (subscribes `StatusChangedEvent`, normalizes/smooths status intensity, then writes per-renderer Shader values through `MaterialPropertyBlock`). Rendering reads Combat state but never changes it.
- **`Game.UI`**: `EnemyHealthBar` (world-space, billboards to camera, one per enemy) and `PlayerHealthBar` (screen-space HUD, smoothed via `Mathf.MoveTowards`). Both consume `DamageReceivedEvent` / `DeathEvent` and filter by `e.TargetId == HealthComponent.Id` (`Id == gameObject.GetInstanceID()`). `Image.fillAmount` requires the UGUI Image be **`Type = Filled`** (a common "bar doesn't move" gotcha). `Game.UI` owns the compact inventory editor and regional spell research workbench, reading spell data from `Game.Skills` and sending ownership requests to `Game.Run`.

---

## Project Status

**Phase**: Three playable archetypes (Warrior / Archer / Wizard) + enemy AI + status effects + health-bar UI + Noita-like spell programming system through trigger recursion. Editor compilation/Animator wiring/Play-mode verification are still performed manually by the developer.

Implemented:
- 9 first-party asmdef modules (Core / Rendering / Combat / Skills / Character / UI / ElementField / Materials / Run)
- `GameLog`, `IGameEvent`, `EventBus<T>`
- `PlayerControllerBase` + `WarriorController` / `ArcherController` / `WizardController`; shared FSM (Grounded / Airborne / Sliding / Dash) + per-character attack states; ground + air attack seams
- Locomotion: camera-relative move, orbit camera, jump (coyote/buffer/split-gravity), slope sliding, dash (locked-direction CrossFade, cooldown + buffer)
- Combat funnel: `IDamageable`/`HealthComponent`, pure `DamagePipeline`, melee `MeleeHitDetector` + `ComboDefinition`/`ComboResolver` combos + blade-trail VFX, projectile hierarchy (`ProjectileBase` → `Arrow`/`Fireball`/`NovaFireball`) with same-team collision registry and `Impacted` trigger seam, reusable `AreaDamage`, unified `StatusController` DoT/reactions
- Archer aimed charge + Wizard spell-cast path (press-instant aim, ground + air); meteor is produced by the same spell-data/interpreter/runtime path as other spells
- Enemy AI: `EnemyControllerBase` (+ Melee/Ranged), `EnemyStateMachine`, `EnemyPerception`, `EnemyDefinition`
- Health-bar UI (`Game.UI`): world-space enemy bars + screen-space player HUD, event-driven
- Spell programming system (`Game.Skills` + `SpellCaster`): data-driven `SpellDefinition`/`WandLoadout`, pure `CastEvaluator`, modifiers/multicast/mana fizzle, deterministic spread, trigger payload recursion on impact, drag-drop wand editor UI
- EditMode tests for `DamagePipeline`, `ComboResolver`, and spell-system pure logic

Next (not yet built):
- Real mitigation formulas in `DamagePipeline` (currently passthrough); expand the existing `StatusController` data/rules when more buffs, debuffs or crowd-control effects are added
- Real mana/resource pool + recovery + UI (the evaluator has fizzle semantics, but runtime currently uses a large placeholder mana value)
- More spell content: timed triggers, tracking/bounce/pierce modifiers, tooltip/loot/save support for the wand UI
- Scene lifecycle manager calling `EventBus<T>.Clear()` on unload
- Remove the remaining `CombatDebugLogger` only after its debug scene is retired

---

## Claude's Scope

Claude only edits `.cs` source files and plain-text config files (`.asmdef`, text-serialized `.asset`/`.controller`, `ProjectSettings/`). **Compilation, Animator wiring, and Play-mode testing are performed manually by the developer in the Unity Editor.** Do not assert that a change "works" — only that it is logically correct based on static code review. Do not create `.meta` files for new assets (Unity generates them); the one exception is moving an existing `.meta` alongside its file during a rename.
