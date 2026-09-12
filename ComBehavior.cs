using AI3.StateMachine;
using NUnit.Framework.Constraints;
using System.Collections;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.AI;

namespace AI3.StateMachine
{
    public class CombatBehavior : AIStateBehavior
    {
        private enum Zone { BelowMinimum, DeadZoneNear, Preferred, DeadZoneFar, BeyondMaximum }
        private enum MovementIntent { None, PursueLastKnown, ApproachRange, RetreatToRange, LateralReposition }

        private const float SUPPRESSIVE_FIRE_CHANCE = 0.15f;
        private const float SUPPRESSIVE_DECISION_INTERVAL = 0.75f;
        private const float REPOSITION_INTERVAL = 0.5f;
        private const int POSITION_SAMPLE_ATTEMPTS = 7;
        private const float POSITION_ANGLE_STEP = 25f;
        private const float RANGE_INSIDE_MARGIN = 0.5f;
        private const float TARGET_REPLAN_DISTANCE = 1f;
        private const float TACTICAL_MOVE_DURATION = 0.6f;
        private const float COMBAT_TURN_SPEED = 360f;
        private const float FIRE_ALIGNMENT_ANGLE = 15f;
        private const int MIN_SHOTS_BEFORE_REPOSITION = 2;
        private const int MAX_SHOTS_BEFORE_REPOSITION = 5;

        private bool _prevUpdateRotation;
        private float _prevStoppingDistance;
        private readonly NavMeshPath _candidatePath = new NavMeshPath();
        private bool _facingOverrideActive;
        private MovementIntent _movementIntent;
        private Vector3 _moveDestination;
        private Vector3 _targetPositionAtPlan;
        private float _repositionTimer;
        private float _nextSuppressiveDecision;
        private int _shotsFromCurrentPosition;
        private int _shotsBeforeReposition;
        private GameObject _targetObject;
        private Vector3 _targetPosition;
        private bool _hasLineOfSight;

        public override void Enter(AIState previous)
        {
            if (Core.NavMeshAgent != null)
            {
                _prevUpdateRotation = Core.NavMeshAgent.updateRotation;
                _prevStoppingDistance = Core.NavMeshAgent.stoppingDistance;
                Core.NavMeshAgent.updateRotation = true;
            }

            _facingOverrideActive = false; // start out folling movement direction, like every other state
            _movementIntent = MovementIntent.None;
            _repositionTimer = 0f;
            _nextSuppressiveDecision = 0f;
            ResetPositionShotBudget();
            RefreshTarget();

            // TODO -> combat-entry bark
        }

        public override void Tick()
        {
            RefreshTarget();

            if (_targetObject == null)
            {
                StopMovement(false);
                SetFacingOverride(false);
                return;
            }

            if (!(Core.AIEquipManager.CurrentWeapon is Gun gun))
            {
                StopMovement(false);
                SetFacingOverride(false);
                return; // melee/no-weapon combat not implemented yet
            }

            float distance = HorizontalDistance(Core.transform.position, _targetPosition);
            Zone zone = ClassifyZone(gun.info, distance);

            HandleMovement(zone, gun.info);
            if (!_hasLineOfSight)
                FaceTarget();

            HandleFiring(zone, gun);
        }

        public override void Exit(AIState next)
        {
            RestoreAgent();
        }

        public override void OnDeath()
        {
            RestoreAgent();
        }

        private void RestoreAgent()
        {
            _movementIntent = MovementIntent.None;
            _facingOverrideActive = false;

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
            _hasLineOfSight = awareness != null && awareness.flags.HasFlag(AIAwarenessFlags.HaveLOS);
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

            Quaternion targetRotation = Quaternion.LookRotation(flatDirection.normalized);
            Core.transform.rotation = Quaternion.RotateTowards(
                Core.transform.rotation,
                targetRotation,
                COMBAT_TURN_SPEED * Time.deltaTime);
        }

        private Zone ClassifyZone(WeaponInfo info, float distance)
        {
            if (distance < info.minCombatRange) return Zone.BelowMinimum;
            if (distance < info.preferredRangeMin) return Zone.DeadZoneNear;
            if (distance <= info.preferredRangeMax) return Zone.Preferred;
            if (distance <= info.maxCombatRange) return Zone.DeadZoneFar;
            return Zone.BeyondMaximum;
        }

        private bool HandleMovement(Zone zone, WeaponInfo info)
        {
            if (Core.NavMeshAgent == null || !Core.NavMeshAgent.isOnNavMesh)
                return false;

            _repositionTimer -= Time.deltaTime;


            // Pursuit has no visible target to face, so NMA owns rotation. All visible-target combat movement
            // keeps the body target-facing and lets AIAnimator's MoveX/MoveY blend tree represent
            // forward, lateral, and backward path velocity in local space
            SetFacingOverride(_hasLineOfSight);

            if (!_hasLineOfSight)
            {
                CancelMovementExcept(MovementIntent.PursueLastKnown);

                if (ShouldReplan(MovementIntent.PursueLastKnown))
                {
                    BeginApproach(0f, MovementIntent.PursueLastKnown);
                    _repositionTimer = REPOSITION_INTERVAL;
                }
                return ContinueMovement(MovementIntent.PursueLastKnown);
            }

            switch (zone)
            {
                case Zone.BelowMinimum:
                case Zone.DeadZoneNear:
                    CancelMovementExcept(MovementIntent.RetreatToRange);
                    if (ShouldReplan(MovementIntent.RetreatToRange))
                    {
                        FindFiringPosition(info, true);
                        _repositionTimer = REPOSITION_INTERVAL;
                    }
                    return ContinueMovement(MovementIntent.RetreatToRange);

                case Zone.Preferred:
                    // The transform-to-target distance is authoritative. NavMeshAgent.remaninigDistance
                    // can lag when updatePosition is false and root motion drives the actual transform.
                    float liveDistance = HorizontalDistance(Core.transform.position, _targetPosition);
                    float innerPreferred = Mathf.Min(
                        info.preferredRangeMax,
                        info.preferredRangeMin + RANGE_INSIDE_MARGIN);
                    float outerPreferred = Mathf.Max(
                        info.preferredRangeMin,
                        info.preferredRangeMax - RANGE_INSIDE_MARGIN);

                    // Continue just inside the band before stopping. Without this hysteresis, animation
                    // coast or a moving target can alternate adjacent frames across a range boundary.
                    if (_movementIntent == MovementIntent.ApproachRange)
                    {
                        if (liveDistance > outerPreferred)
                            return ContinueMovement(MovementIntent.ApproachRange);
                        StopMovement(true);
                    }
                    else if (_movementIntent == MovementIntent.RetreatToRange)
                    {
                        if (liveDistance < innerPreferred)
                            return ContinueMovement(MovementIntent.RetreatToRange);
                        StopMovement(true);
                    }

                    CancelMovementExcept(MovementIntent.LateralReposition);
                    if (_movementIntent == MovementIntent.LateralReposition)
                    {
                        if (ContinueMovement(MovementIntent.LateralReposition))
                            return true;
                    }
                    
                    if (_shotsFromCurrentPosition >= _shotsBeforeReposition && _repositionTimer <= 0f)
                    {
                        FindFiringPosition(info, false);
                        _repositionTimer = REPOSITION_INTERVAL;
                    }
                    break;

                case Zone.DeadZoneFar:
                case Zone.BeyondMaximum:
                    CancelMovementExcept(MovementIntent.ApproachRange);
                    if (ShouldReplan(MovementIntent.ApproachRange))
                    {
                        BeginApproach(info.preferredRangeMax, MovementIntent.ApproachRange);
                        _repositionTimer = REPOSITION_INTERVAL;
                    }
                    return ContinueMovement(MovementIntent.ApproachRange);
            }

            if (_movementIntent != MovementIntent.None)
                return true;

            HoldFiringPosition();
            return false;
        }

        private void CancelMovementExcept(MovementIntent allowedIntent)
        {
            if (_movementIntent == MovementIntent.None || _movementIntent == allowedIntent)
                return;

            StopMovement(false);
            _repositionTimer = 0f;
        }

        private bool ShouldReplan(MovementIntent desiredIntent)
        {
            if (_repositionTimer > 0f)
                return false;

            return _movementIntent != desiredIntent ||
                HorizontalDistance(_targetPosition, _targetPositionAtPlan) >= TARGET_REPLAN_DISTANCE;
        }

        private bool ContinueMovement(MovementIntent expectedIntent)
        {
            if (_movementIntent != expectedIntent)
                return false;

            if (ReachedDestination())
            {
                StopMovement(true);
                return false;
            }

            SetFacingOverride(expectedIntent != MovementIntent.PursueLastKnown);
            return true;
        }

        /// <summary>When active, combat code owns body rot so the target remains forward while
        /// AIAnimator converts the agent's path velocity into local directional blend params</summary>
        private void SetFacingOverride(bool active)
        {
            if (_facingOverrideActive == active || Core.NavMeshAgent == null)
                return;

            _facingOverrideActive = active;
            Core.NavMeshAgent.updateRotation = !active;
        }

        /// <summary>
        /// Hands the target pos to NMA so normal pathfinding can route around walls.
        /// The margin is subtracted: adding it would stop just outside the preferred band and cause
        /// repeated DeadZoneFar approach results.
        /// </summary>
        /// <param name="targetDistance"></param>
        /// <param name="intent"></param>
        private void BeginApproach(float targetDistance, MovementIntent intent)
        {
            SetFacingOverride(intent != MovementIntent.PursueLastKnown);
            Core.NavMeshAgent.stoppingDistance = Mathf.Max(0f, targetDistance - RANGE_INSIDE_MARGIN);
            Vector3 nextDestination = _targetPosition;
            if (Core.NavMeshAgent.SetDestination(nextDestination))
            {
                _moveDestination = nextDestination;
                _targetPositionAtPlan = _targetPosition;
                _movementIntent = intent;
            }
            else if (_movementIntent != intent)
            {
                _movementIntent = MovementIntent.None;
            }
        }

        /// <summary>
        /// Tries short radial, lateral, and fallback moves around the target.
        /// Each move is capped to roughly 0.6 seconds of travel
        /// </summary>
        /// <param name="info"></param>
        /// <param name="retreat"></param>
        private void FindFiringPosition(WeaponInfo info, bool retreat)
        {
            Vector3 flatSelf = Core.transform.position; flatSelf.y = 0f;
            Vector3 flatTarget = _targetPosition; flatTarget.y = 0f;

            Vector3 radial = flatSelf - flatTarget;
            radial = radial.sqrMagnitude > 0.0001f ? radial.normalized : Core.transform.forward;
            float minRange = info.preferredRangeMin + RANGE_INSIDE_MARGIN;
            float maxRange = Mathf.Max(minRange, info.preferredRangeMax - RANGE_INSIDE_MARGIN);
            float desiredRange = retreat
                ? minRange
                : Mathf.Clamp(HorizontalDistance(flatSelf, flatTarget), minRange, maxRange);
            int firstSide = Random.value < 0.5f ? -1 : 1;

            for (int i = 0; i < POSITION_SAMPLE_ATTEMPTS; i++)
            {
                int magnitude = retreat ? (i + 1) / 2 : i / 2 + 1;
                int side = i % 2 == 0 ? firstSide : -firstSide;
                float angle = retreat && i == 0 ? 0f : side * magnitude * POSITION_ANGLE_STEP;
                Vector3 candidateDir = Quaternion.Euler(0f, angle, 0f) * radial;
                Vector3 candidate = flatTarget + candidateDir * desiredRange;

                float maxMoveDistance = Mathf.Max(Core.NavMeshAgent.radius * 2f, Core.NavMeshAgent.speed * TACTICAL_MOVE_DURATION);
                Vector3 moveOffset = Flatten(candidate - flatSelf);
                if (moveOffset.magnitude > maxMoveDistance)
                    candidate = flatSelf + moveOffset.normalized * maxMoveDistance;

                candidate.y = Core.transform.position.y;

                if (Vector3.SqrMagnitude(Flatten(candidate - Core.transform.position)) < 1f)
                    continue;

                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, Core.NavMeshAgent.areaMask) &&
                    TryBeginPath(hit.position, retreat ? MovementIntent.RetreatToRange : MovementIntent.LateralReposition))
                    return;
            }

            // A refresh may fail transiently near a NavMesh edge. Preserve an existing path of the
            // same intent rather than dropping into stationary firing while the agent still moves.
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0f;
            return value;
        }

        private bool TryBeginPath(Vector3 destination, MovementIntent intent)
        {
            if (!Core.NavMeshAgent.CalculatePath(destination, _candidatePath) ||
                _candidatePath.status != NavMeshPathStatus.PathComplete)
                return false;

            SetFacingOverride(intent != MovementIntent.PursueLastKnown);
            Core.NavMeshAgent.stoppingDistance = 0.15f;
            if (!Core.NavMeshAgent.SetPath(_candidatePath))
                return false;

            _moveDestination = destination;
            _targetPositionAtPlan = _targetPosition;
            _movementIntent = intent;
            return true;
        }

        private bool ReachedDestination()
        {
            // Root motion owns the transform, so use physical pos first. stoppingDistance is intentionally large for approach paths and tiny for sampled firing positions
            float arrivalRadius = Core.NavMeshAgent.stoppingDistance + 0.25f;
            if (HorizontalDistance(Core.transform.position, _moveDestination) <= arrivalRadius)
                return true;

            if (Core.NavMeshAgent.pathPending)
                return false;

            return !Core.NavMeshAgent.hasPath ||
                Core.NavMeshAgent.pathStatus == NavMeshPathStatus.PathInvalid;
        }

        private void StopMovement(bool completedPositionChange)
        {
            _movementIntent = MovementIntent.None;
            if (Core.NavMeshAgent != null && Core.NavMeshAgent.isOnNavMesh && Core.NavMeshAgent.hasPath)
                Core.NavMeshAgent.ResetPath();
            if (completedPositionChange)
                ResetPositionShotBudget();
        }

        private void HoldFiringPosition()
        {
            _movementIntent = MovementIntent.None;
            if (Core.NavMeshAgent.hasPath)
                Core.NavMeshAgent.ResetPath();
            SetFacingOverride(true);
        }

        private void ResetPositionShotBudget()
        {
            _shotsFromCurrentPosition = 0;
            _shotsBeforeReposition = Random.Range(
                MIN_SHOTS_BEFORE_REPOSITION,
                MAX_SHOTS_BEFORE_REPOSITION + 1);
        }

        private void HandleFiring(Zone zone, Gun gun)
        {
            if (!_hasLineOfSight || !IsFacingTarget())
                return;

            if (zone == Zone.BelowMinimum)
                return;

            if (gun.CurrentState != EquippableObject.EquipState.Idle)
                return;

            if (zone == Zone.BeyondMaximum)
            {
                if (Time.time < _nextSuppressiveDecision)
                    return;

                _nextSuppressiveDecision = Time.time + SUPPRESSIVE_DECISION_INTERVAL;
                if (Random.value > SUPPRESSIVE_FIRE_CHANCE)
                    return;
            }

            Core.AIEquipManager.FireAtTarget(_targetPosition);
            _shotsFromCurrentPosition++;
            if (_shotsFromCurrentPosition >= _shotsBeforeReposition)
                _repositionTimer = 0f;
        }

        private bool IsFacingTarget()
        {
            Vector3 flatDirection = Flatten(_targetPosition - Core.transform.position);
            if (flatDirection.sqrMagnitude < 0.0001f)
                return true;

            return Vector3.Angle(Core.transform.forward, flatDirection) <= FIRE_ALIGNMENT_ANGLE;
        }
    }
}
