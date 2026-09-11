using AI3;
using UnityEngine;
using UnityEngine.AI;

namespace AI3.StateMachine
{
    /// <summary>
    /// Combat AI: Combat Area range-band behavior
    /// No cover-seeking yet (none exists)
    ///
    /// Deliberately no HoldDecay() call: AICore.UpdateAlertness() already pins AIAlertness.level at
    /// High for up to AI_TIME_MAX_ALERT while in contact, independent of Pulse's capacitor discharge.
    /// Losing the target (LOS gone long enough) is handled entirely by AIStateManager's normal
    /// alertness-driven transition into Agitated -- this class doesn't watch for that itself.
    ///
    /// IMPORTANT: movement here is root-motion-driven (AIAnimator sets Animator.applyRootMotion = true
    /// and NavMeshAgent.updatePosition = false), meaning the character physically translates in
    /// whatever direction the currently-playing clip moves it -- forward, relative to its own facing,
    /// for the WALK/RUN clips that exist today (no backward/strafe clips are defined). That means
    /// facing MUST track movement direction while actually relocating, or any locomotion clip will
    /// drag the character toward wherever it's facing regardless of the NavMeshAgent's own path.
    /// Facing is therefore only overridden to point at the target while stationary (Preferred zone,
    /// holding position to shoot); while approaching, movement-direction-facing already points
    /// roughly at the target anyway
    ///
    /// Ranged combat only for now: if the AI isn't holding a Gun, Tick() is a no-op. No AI-specific
    /// aim error yet either -- shots aim exactly at the tracked target position, with only the
    /// weapon's own WeaponInfo.maxSpread (shared with the player) adding inaccuracy. Also no
    /// deliberate burst/pause pacing -- fires every tick the gun is idle and conditions allow, so a
    /// weapon's own fireRateRPM/magazineSize entirely determine how "controlled" it looks (a 10-round
    /// mag at 600rpm empties in ~1s, non-stop). Add pacing here later if that's not the intended feel.
    /// </summary>
    public class HostileBehavior : AIStateBehavior
    {
        private enum Zone { BelowMinimum, DeadZoneNear, Preferred, DeadZoneFar, BeyondMaximum }

        private const float SUPPRESSIVE_FIRE_CHANCE = 0.15f; // flat per-Tick() chance while BeyondMaximum, not framerate-normalized
        private const float REPOSITION_INTERVAL = 0.5f;      // throttle for non-urgent repositioning (everything except BelowMinimum)
        private const int RETREAT_SAMPLE_ATTEMPTS = 5;        // candidate angles to try if the direct-away point isn't on the NavMesh
        private const float APPROACH_STOP_MARGIN = 0.5f;     // small cushion against root-motion/blend lag so approach doesn't nudge past the edge of Preferred

        private bool _prevUpdateRotation;
        private float _prevStoppingDistance;
        private bool _facingOverrideActive; // true only while stationary in Preferred, manually facing the target
        private float _repositionTimer;
        private GameObject _targetObject;
        private Vector3 _targetPosition;

        public override void Enter(AIState previous)
        {
            if (Core.NavMeshAgent != null)
            {
                _prevUpdateRotation = Core.NavMeshAgent.updateRotation;
                _prevStoppingDistance = Core.NavMeshAgent.stoppingDistance;
            }

            _facingOverrideActive = false; // start out following movement direction, like every other state
            _repositionTimer = 0f;
            RefreshTarget();

            // TODO: draw weapon / combat-entry bark and animation.
        }

        public override void Tick()
        {
            RefreshTarget();

            if (_targetObject == null)
                return; // shouldn't normally happen while Hostile, but nothing to fight if it does

            if (_facingOverrideActive)
                FaceTarget(); // only while stationary -- see class-level note on root motion

            if (!(Core.AIEquipManager.CurrentWeapon is Gun gun))
                return; // melee/no-weapon combat not implemented yet

            float distance = HorizontalDistance(Core.transform.position, _targetPosition);
            Zone zone = ClassifyZone(gun.info, distance);

            HandleMovement(zone, gun.info);
            HandleFiring(zone, gun);
        }

        public override void Exit(AIState next) => RestoreAgent();
        public override void OnDeath() => RestoreAgent();

        private void RestoreAgent()
        {
            if (Core.NavMeshAgent == null)
                return;

            Core.NavMeshAgent.updateRotation = _prevUpdateRotation;
            Core.NavMeshAgent.stoppingDistance = _prevStoppingDistance;
            if (Core.NavMeshAgent.isOnNavMesh)
                Core.NavMeshAgent.ResetPath();
        }

        /// <summary>Re-reads the current highest-priority awareness every tick -- same principle as
        /// Suspicious/AIAreaSearchBehavior's stimulus tracking: always fight whoever's currently most
        /// relevant, not necessarily whoever originally triggered Hostile.</summary>
        private void RefreshTarget()
        {
            AIAwareness awareness = Core.AISenses.GetHighAwareness(AISenses.AISensesGetHighFlags.Alerting);
            _targetObject = awareness?.targetObject;
            _targetPosition = awareness != null ? awareness.lastPos : Core.transform.position;
        }

        private bool HasLineOfSight()
        {
            AIAwareness awareness = Core.AISenses.GetHighAwareness(AISenses.AISensesGetHighFlags.Alerting);
            return awareness != null && awareness.flags.HasFlag(AIAwarenessFlags.HaveLOS);
        }

        /// <summary>Distance in the XZ plane only -- see the earlier fix note: a vertical offset between
        /// the AI's root and the tracked (likely eye/head-height) target position would otherwise be
        /// baked permanently into every zone calculation, a gap the AI can never close by walking.</summary>
        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private void FaceTarget()
        {
            Vector3 flatDirection = _targetPosition - Core.transform.position;
            flatDirection.y = 0f;
            if (flatDirection.sqrMagnitude < 0.0001f)
                return;

            Core.transform.rotation = Quaternion.LookRotation(flatDirection.normalized);
        }

        private Zone ClassifyZone(WeaponInfo info, float distance)
        {
            if (distance < info.minCombatRange) return Zone.BelowMinimum;
            if (distance < info.preferredRangeMin) return Zone.DeadZoneNear;
            if (distance <= info.preferredRangeMax) return Zone.Preferred;
            if (distance <= info.maxCombatRange) return Zone.DeadZoneFar;
            return Zone.BeyondMaximum;
        }

        private void HandleMovement(Zone zone, WeaponInfo info)
        {
            if (Core.NavMeshAgent == null || !Core.NavMeshAgent.isOnNavMesh)
                return;

            _repositionTimer -= Time.deltaTime;

            if (!HasLineOfSight())
            {
                SetFacingOverride(false); // Let the AI face where it's walking
                if (_repositionTimer <= 0f)
                {
                    // Approach the exact last known location with a minimal stopping distance
                    ApproachTo(0f);
                    _repositionTimer = REPOSITION_INTERVAL;
                }
                return; // Skip the standard zone switch statement below
            }

            SetFacingOverride(true);

            switch (zone)
            {
                case Zone.BelowMinimum:
                    RetreatFrom(info.preferredRangeMin);
                    _repositionTimer = 0f;
                    break;

                case Zone.DeadZoneNear:
                    if (_repositionTimer <= 0f)
                    {
                        RetreatFrom(info.preferredRangeMin);
                        _repositionTimer = REPOSITION_INTERVAL;
                    }
                    break;

                case Zone.Preferred:
                    Core.NavMeshAgent.ResetPath();
                    break;

                case Zone.DeadZoneFar:
                case Zone.BeyondMaximum:
                    if (_repositionTimer <= 0f)
                    {
                        ApproachTo(info.preferredRangeMax);
                        _repositionTimer = REPOSITION_INTERVAL;
                    }
                    break;
            }
        }

        /// <summary>Toggles NavMeshAgent.updateRotation to match: manual facing (FaceTarget) only while
        /// stationary, agent-driven facing (matches movement direction) any time we're relocating --
        /// see the class-level note on why this matters for root-motion-driven movement.</summary>
        private void SetFacingOverride(bool active)
        {
            if (_facingOverrideActive == active || Core.NavMeshAgent == null)
                return;

            _facingOverrideActive = active;
            Core.NavMeshAgent.updateRotation = !active;
        }

        /// <summary>
        /// Hands the real target position straight to NavMeshAgent and lets its own pathfinding route
        /// around corners/walls -- this is what fixes "doesn't move at all" when the target is out of
        /// sight around a corner. stoppingDistance controls how close the agent actually gets, with a
        /// small extra margin as cheap insurance against root-motion/animation-blend lag overshooting
        /// the exact boundary.
        /// </summary>
        private void ApproachTo(float targetDistance)
        {
            Core.NavMeshAgent.stoppingDistance = targetDistance + APPROACH_STOP_MARGIN;
            Core.NavMeshAgent.SetDestination(_targetPosition);
        }

        /// <summary>
        /// Retreating has no NavMeshAgent-native primitive (stoppingDistance only helps when
        /// approaching a destination), so this still needs an explicit fallback point -- but tries
        /// several candidate angles around "directly away" rather than one single point, so a nearby
        /// wall/corner doesn't just silently fail to produce any destination at all.
        /// </summary>
        private void RetreatFrom(float targetDistance)
        {
            Vector3 flatSelf = Core.transform.position; flatSelf.y = 0f;
            Vector3 flatTarget = _targetPosition; flatTarget.y = 0f;

            Vector3 awayDir = flatSelf - flatTarget;
            awayDir = awayDir.sqrMagnitude > 0.0001f ? awayDir.normalized : Core.transform.forward;

            for (int i = 0; i < RETREAT_SAMPLE_ATTEMPTS; i++)
            {
                float angle = i == 0 ? 0f : (i % 2 == 1 ? 1 : -1) * (i * 25f);
                Vector3 candidateDir = Quaternion.Euler(0f, angle, 0f) * awayDir;
                Vector3 candidate = flatTarget + candidateDir * targetDistance;
                candidate.y = Core.transform.position.y;

                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, targetDistance, NavMesh.AllAreas))
                {
                    Core.NavMeshAgent.SetDestination(hit.position);
                    return;
                }
            }

            // Boxed in on all tried angles -- hold position and keep firing rather than get stuck
            // mid-computation with a stale or nonexistent destination.
            Core.NavMeshAgent.ResetPath();
        }

        private void HandleFiring(Zone zone, Gun gun)
        {
            if (!HasLineOfSight())
                return;

            // Retreating means facing follows movement direction (away from the target) now, not the
            // target itself -- see class-level note. No coherent way to fire while facing away without
            // a dedicated backward/strafe animation, so this deliberately drops the doc's "still fires
            // while backing away" detail until those assets exist.
            if (zone == Zone.BelowMinimum)
                return;

            if (gun.CurrentState != EquippableObject.EquipState.Idle)
                return;

            if (zone == Zone.BeyondMaximum && Random.value > SUPPRESSIVE_FIRE_CHANCE)
                return; // "only fire sporadically with suppressive fire... may not attack at all"

            Core.AIEquipManager.FireAtTarget(_targetPosition);
        }
    }
}