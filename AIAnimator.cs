using AI3.Model;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.VisualScripting;
using UnityEngine;

namespace AI3
{
    public class AIAnimator : MonoBehaviour, IAIComponent
    {
        public const int LAYERS = 2;
        public const int UPPERBODY = 0;
        public const int LOWERBODY = 1;

        // Lock priority levels. A higher-priority Play() call can preempt a lower-priority lock
        // without needing bypassLock (which also force-cancels running AIAnimatorOnExit behaviours
        public const int PRIORITY_MOVEMENT = 0;
        public const int PRIORITY_ACTION = 1;      // firing, reload, push button, etc.
        public const int PRIORITY_REACTION = 2;    // flinch, stagger, shocked/stunned
        public const int PRIORITY_CRITICAL = 3;    // death and other non-interruptible anims

        // Source-of-truth lists for building the movement-animation lookup table below.
        // single place to touch when adding a weapon class / alertness tier / speed tier.
        private List<string> equippedObjectParams = new List<string> { "UNARMED", "H1", "H2", "MELEE" };
        private List<string> alertnessParams = new List<string> { "LOWEST", "LOW", "MODERATE", "HIGH" };
        private List<string> speedParams = new List<string> { "IDLE", "WALK", "RUN" };

        public Animator Animator;

        private AICore _aiCore;
        private Animations[] _currentAnimation;
        private int[] _layerLockPriority;
        Action<int> _defaultAnimation;

        // Maps (weaponClass, speed, alertness) -> Animations. Built once in Start().
        // A missing combo (e.g. no H1_RUN_MODERATE state defined) resolves to a logged
        // fallback instead of throwing mid-gameplay.
        private Dictionary<(string weapon, string speed, string alertness), Animations> _movementAnimLookup;

        private Vector2 Velocity;
        private Vector2 SmoothDeltaPosition;
        private bool ShouldMove;

        private Coroutine[] _pendingExitCoroutines = new Coroutine[LAYERS];

        private void Start()
        {
            _aiCore = GetComponent<AICore>();

            _layerLockPriority = new int[LAYERS];
            _currentAnimation = new Animations[LAYERS];
            _defaultAnimation = DefaultAnimation;

            for (int i = 0; i < LAYERS; i++)
            {
                _layerLockPriority[i] = PRIORITY_MOVEMENT;
                _currentAnimation[i] = Animations.UNARMED_IDLE;
            }

            BuildMovementAnimationLookup();

            // ****************************************************************************
            // Changes to enable root motion
            Animator.applyRootMotion = true;
            _aiCore.NavMeshAgent.updatePosition = false;
            _aiCore.NavMeshAgent.updateRotation = true;
        }

        /// <summary>
        /// Builds the (weapon, speed, alertness) -> Animations lookup from the enum's actual
        /// defined values, so combos that don't exist (e.g. H1_RUN_MODERATE) are simply
        /// absent from the table rather than causing a parse failure later
        /// </summary>
        private void BuildMovementAnimationLookup()
        {
            _movementAnimLookup = new Dictionary<(string, string, string), Animations>();
            var allAnimNames = Enum.GetNames(typeof(Animations));

            foreach (var weapon in equippedObjectParams)
            {
                foreach (var speed in speedParams)
                {
                    foreach (var alertness in alertnessParams)
                    {
                        // TODO -> UNARMED/MELEE don't carry alertness suffixes today
                        // only build H1_/H2_ combos here. Extend this if that changes.
                        if (weapon != "H1" && weapon != "H2") continue;

                        string candidate = $"{weapon}_{speed}_{alertness}";
                        if (Array.IndexOf(allAnimNames, candidate) >= 0)
                        {
                            _movementAnimLookup[(weapon, speed, alertness)] =
                                (Animations)Enum.Parse(typeof(Animations), candidate);
                        }
                        // else: intentionally left out of the table; CheckMovementAnimations
                        // will fall back gracefully and log a warning if this combo is requested.
                    }
                }
            }
        }

        private void Update()
        {
            if (!PlayerUI.GameIsPaused && !_aiCore.IsDead())
            {
                CheckTopAnimation();
                CheckBottomAnimation();
                SynchronizeAnimatorAndNavMeshAgent();
            }
        }

        public void DisableAnimatorMotion()
        {
            Animator.applyRootMotion = false;
            _aiCore.NavMeshAgent.updatePosition = false;
            _aiCore.NavMeshAgent.updateRotation = false;
        }

        /// <summary>
        /// Override default rootMotion behavior
        /// </summary>
        private void OnAnimatorMove()
        {
            Vector3 deltaPosition = Animator.deltaPosition;

            // Only apply movement if it's significant (stops micro-drift)
            if (deltaPosition.magnitude > 0.001f)
            {
                Vector3 rootPos = Animator.rootPosition;
                if (!_aiCore.IsDead())
                    rootPos.y = _aiCore.NavMeshAgent.nextPosition.y;
                transform.position = rootPos;
                _aiCore.NavMeshAgent.nextPosition = rootPos;
            }
        }

        private void SynchronizeAnimatorAndNavMeshAgent()
        {
            // Guard against zero deltaTime on paused/loading frames
            if (Time.unscaledDeltaTime <= 0f) return;

            Vector3 worldDeltaPos = _aiCore.NavMeshAgent.nextPosition - transform.position;
            worldDeltaPos.y = 0;

            float dx = Vector3.Dot(transform.right, worldDeltaPos);
            float dy = Vector3.Dot(transform.forward, worldDeltaPos);
            Vector2 deltaPos = new Vector2(dx, dy);

            float smooth = Mathf.Min(1f, Time.unscaledDeltaTime / 0.1f);
            SmoothDeltaPosition = Vector2.Lerp(SmoothDeltaPosition, deltaPos, smooth);

            // Sanity check to recover if SmoothDeltaPosition ever receives NaN from physics/transform glitches
            if (float.IsNaN(SmoothDeltaPosition.x) || float.IsNaN(SmoothDeltaPosition.y))
            {
                SmoothDeltaPosition = Vector2.zero;
            }

            float deltaMagnitude = worldDeltaPos.magnitude;
            if (deltaMagnitude > _aiCore.NavMeshAgent.radius / 2f)
            {
                transform.position = Vector3.Lerp(Animator.rootPosition, _aiCore.NavMeshAgent.nextPosition, smooth);
            }
        }

        /////////////////////////////////////////////////////
        // Animator Controller methods
        public Animations GetCurrentAnimation(int layer)
        {
            return _currentAnimation[layer];
        }

        // Kept for backward compatibility with existing callers. Treats a plain bool lock
        // as PRIORITY_ACTION (i.e. "blocks ordinary movement/action calls but not reactions
        // or death"). Prefer the (bool, int, int priority) overload below for new code.
        public void SetLocked(bool lockLayer, int layer)
        {
            _layerLockPriority[layer] = lockLayer ? PRIORITY_ACTION : PRIORITY_MOVEMENT;
        }

        public void SetLocked(bool lockLayer, int layer, int priority)
        {
            _layerLockPriority[layer] = lockLayer ? priority : PRIORITY_MOVEMENT;
        }

        // TODO -> have AI gun manager play shooting/reload anims based on weapon type
        // Play(Animations.PISTOL_FIRE, UPPERBODY, true, false, PRIORITY_ACTION);
        // Play(Animations.PISTOL_RELOAD, UPPERBODY, true, false, PRIORITY_ACTION);
        public void Play(Animations animation, int layer, bool lockLayer, bool bypassLock, float crossfade = 0.2f)
        {
            Play(animation, layer, lockLayer, bypassLock, PRIORITY_MOVEMENT, crossfade);
        }

        /// <summary>
        /// Plays an animation on the given layer. A call only proceeds if its priority is >=
        /// the layer's current lock priority (or bypassLock is set, which additionally cancels
        /// any running AIAnimatorOnExit behaviours on this layer - use for true "must not be
        /// interrupted" cases like death, not general priority preemption).
        /// </summary>
        public void Play(Animations animation, int layer, bool lockLayer, bool bypassLock, int priority, float crossfade = 0.2f)
        {
            if (animation == Animations.NONE)
            {
                _defaultAnimation(layer);
                return;
            }

            if (_layerLockPriority[layer] > priority && !bypassLock) return;
            _layerLockPriority[layer] = lockLayer ? priority : PRIORITY_MOVEMENT;

            if (bypassLock) // Critical anims need to cancel all other anims
                CancelPendingOnExit(layer);

            if (_currentAnimation[layer] == animation) return;

            // Whatever was previously playing on this layer is about to be superseded -- cancel its
            // pending AIAnimatorOnExit coroutine (if any). Without this, a stale timer scheduled
            // against the OLD clip's length can fire later and forcibly snap this layer back to a
            // fallback animation in the middle of whatever plays next (this was silently cutting off
            // reload animations mid-play, and causing repeated fire/idle flicker during sustained fire).
            CancelPendingOnExit(layer);

            _currentAnimation[layer] = animation;
            Animator.CrossFade(Animator.StringToHash(_currentAnimation[layer].ToString()), crossfade, layer);
        }

        /// <summary>
        /// Schedules an animation transition after a state finishes playing.
        /// Automatically cancels any existing pending exit coroutine on this layer.
        /// </summary>
        public void ScheduleOnExit(int layer, float duration, float crossfade, Animations nextAnimation, bool lockNextLayer)
        {
            CancelPendingOnExit(layer);
            _pendingExitCoroutines[layer] = StartCoroutine(Co_WaitAndExit(layer, duration, crossfade, nextAnimation, lockNextLayer));
        }

        private IEnumerator Co_WaitAndExit(int layer, float duration, float crossfade, Animations nextAnimation, bool lockNextLayer)
        {
            yield return new WaitForSeconds(Mathf.Max(0f, duration - crossfade));
            _pendingExitCoroutines[layer] = null;
            SetLocked(false, layer);
            Play(nextAnimation, layer, lockNextLayer, false, crossfade);
        }

        private void CancelPendingOnExit(int layer)
        {
            if (_pendingExitCoroutines[layer] != null)
            {
                StopCoroutine(_pendingExitCoroutines[layer]);
                _pendingExitCoroutines[layer] = null;
            }
        }

        public Animations GetDeathAnimation()
        {
            //Animations[] deathAnimList = new Animations[] { Animations.M01_DEATH, Animations.M01_DEATH }; // TODO -> add more death anims
            // May want to add param to method for AI type (can have different deaths for male, female, humans, non-humans, etc.)
            //int deathAnimInt = (int)UnityEngine.Random.Range(0, 1);
            //return deathAnimList[deathAnimInt];
            return Animations.DIE_FORWARD_2;
        }

        private void CheckTopAnimation()
        {
            CheckMovementAnimations(UPPERBODY);
        }

        private void CheckBottomAnimation()
        {
            CheckMovementAnimations(LOWERBODY);
        }

        private const string PARAM_MOVE_X = "MoveX";
        private const string PARAM_MOVE_Y = "MoveY";
        private const float MOVE_BLEND_DAMP_TIME = 0.15f; // tune empirically 

        private void CheckMovementAnimations(int layer)
        {
            // play move animation depending on speed/weapon/awareness/etc. TODO -> implement weapon check
            // TODO -> need a different way to override and trigger RUN -> maybe check on alertness + distance to target?

            if (Time.unscaledDeltaTime <= 0f) return;
            Velocity = SmoothDeltaPosition / Time.unscaledDeltaTime;

            float stopDist = Mathf.Max(_aiCore.NavMeshAgent.stoppingDistance, 0.01f);
            if (_aiCore.NavMeshAgent.remainingDistance <= stopDist)
            {
                float lerpFactor = Mathf.Clamp01(_aiCore.NavMeshAgent.remainingDistance / stopDist);
                Velocity = Vector2.Lerp(Vector2.zero, Velocity, lerpFactor);
            }

            // Safety fallback to prevent NaN from reaching Animator.SetFloat
            if (float.IsNaN(Velocity.x) || float.IsNaN(Velocity.y))
            {
                Velocity = Vector2.zero;
            }

            ShouldMove = Velocity.magnitude > 0.25f && _aiCore.NavMeshAgent.remainingDistance > _aiCore.NavMeshAgent.stoppingDistance;

            // TODO -> conditional logic for unarmed (don't include alertness level, etc.)
            // TODO -> get actual equipped weapon class ("H1"/"H2"/"MELEE"/"UNARMED") from weapon data
            // instead of hardcoding "H1" - hardcoded here to match existing behavior pre-refactor.
            string weaponParam = "H1";
            string alertnessParam = _aiCore.AIAlertness.level.ToString().ToUpper();

            // HIGH-alertness H1/H2 combat movement uses a single looping directional blend tree
            // instead of discrete IDLE/WALK/RUN states -- lets the AI move in any direction relative
            // to its facing (needed for combat strafing/retreating-while-facing-target) and blend
            // continuously between walk and run speed rather than snapping between two clips.
            if (alertnessParam == "HIGH" && (weaponParam == "H1" || weaponParam == "H2"))
            {
                PlayCombatMovementBlend(weaponParam, layer);
                return;
            }

            string speedParam = Velocity.magnitude == 0 || !ShouldMove ? "IDLE" : Velocity.magnitude < 50 ? "WALK" : "RUN";    // TODO -> convert to enums, const for check?
                                                                                                                               // also need to sort out velocity magnitude. sometimes it's as low as 0.4?

            Play(ResolveMovementAnimation(weaponParam, speedParam, alertnessParam), layer, false, false);
        }

        /// <summary>
        /// Drives the HIGH-alertness combat locomotion blend tree. Play() is still called every tick
        /// to ensure we're actually in the blend tree state -- it's a harmless no-op after the first
        /// frame via the existing dedup check -- but the parameters driving the blend within that
        /// state are updated continuously every frame regardless, since Animator.SetFloat is a
        /// separate call from CrossFade/Play and isn't gated by that dedup at all.
        ///
        /// MoveX/MoveY convention: local-space velocity (Velocity.x = sideways, Velocity.y =
        /// forward/back, computed in SynchronizeAnimatorAndNavMeshAgent) normalized against the
        /// agent's own configured max speed, so ~0.5 lands near "walk" and ~1.0 near "run" regardless
        /// of Velocity's raw scale. NOTE: Velocity's raw scale has an existing open question (see the
        /// TODO in CheckMovementAnimations above) -- if the blend looks off, check that before
        /// assuming the blend tree's own motion field thresholds are wrong.
        ///
        /// No AIAnimatorOnExit is attached to this state -- unlike FIRE/RELOAD, it has no natural
        /// "finish" to return from; it's simply exited via an ordinary Play() call to something else
        /// (e.g. leaving Hostile, or a different layer state like FIRE/RELOAD superseding it).
        /// </summary>
        private void PlayCombatMovementBlend(string weapon, int layer)
        {
            Animations blendState = weapon == "H2" ? Animations.H2_COMBAT_MOVEMENT : Animations.H1_COMBAT_MOVEMENT;
            Play(blendState, layer, false, false);

            // Animator params are controller-wide, not layer-specific. Both layers enter the combat movement state,
            // but writing the same damped values twice per frame changes the damping repsonse and does unnecessary work.
            // Let lowerbody own the param updates
            if (layer != LOWERBODY)
                return;

            float maxSpeed = Mathf.Max(_aiCore.NavMeshAgent.speed, 0.01f);
            float moveX = Mathf.Clamp(Velocity.x / maxSpeed, -1f, 1f);
            float moveY = Mathf.Clamp(Velocity.y / maxSpeed, -1f, 1f);

            if (float.IsNaN(moveX))
                moveX = 0f;
            if (float.IsNaN(moveY))
                moveY = 0f;

            Animator.SetFloat(PARAM_MOVE_X, moveX, MOVE_BLEND_DAMP_TIME, Time.deltaTime);
            Animator.SetFloat(PARAM_MOVE_Y, moveY, MOVE_BLEND_DAMP_TIME, Time.deltaTime);
        }

        /// <summary>
        /// Looks up the movement animation for a given (weapon, speed, alertness) combo.
        /// Falls back to the weapon's IDLE_LOWEST anim (or UNARMED_IDLE as a last resort) and
        /// logs a warning if the exact combo has no matching Animator state defined yet,
        /// instead of throwing and potentially halting this AI's Update() loop.
        /// </summary>
        private Animations ResolveMovementAnimation(string weapon, string speed, string alertness)
        {
            if (_movementAnimLookup.TryGetValue((weapon, speed, alertness), out var anim))
                return anim;

            Debug.LogWarning($"[AIAnimator] No animation defined for {weapon}_{speed}_{alertness} " +
                              $"on '{name}'. Falling back to idle. Add this state to the Animator " +
                              $"Controller and Animations enum.");

            if (_movementAnimLookup.TryGetValue((weapon, "IDLE", "LOWEST"), out var fallback))
                return fallback;

            return Animations.UNARMED_IDLE;
        }

        private void DefaultAnimation(int layer)
        {
            if (layer == UPPERBODY)
                CheckTopAnimation();
            else
                CheckBottomAnimation();
        }

        /////////////////////////////////////////////////////

        /////////////////////////////////////////////////////
        public enum Animations
        {
            UNARMED_IDLE,
            UNARMED_WALK,
            UNARMED_RUN,

            COWER,
            FLEE,

            H1_FIRE,
            H1_FIRE_CROUCH,
            H2_FIRE,
            H2_FIRE_CROUCH,

            H1_IDLE_LOWEST,
            H1_IDLE_LOW,
            H1_IDLE_MODERATE,
            H1_WALK_LOWEST,
            H1_WALK_LOW,
            H1_WALK_MODERATE,
            H1_COMBAT_MOVEMENT, // 2D Freeform Directional blend tree (MoveX/MoveY) -- replaces H1_IDLE_HIGH/H1_WALK_HIGH/H1_RUN_HIGH

            H2_IDLE_LOWEST,
            H2_IDLE_LOW,
            H2_IDLE_MODERATE,
            H2_WALK_LOWEST,
            H2_WALK_LOW,
            H2_WALK_MODERATE,
            H2_COMBAT_MOVEMENT, // same as H1_COMBAT_MOVEMENT, for the H2 weapon class

            UNARMED_IDLE_CROUCH,
            H1_IDLE_CROUCH,
            H2_IDLE_CROUCH,

            H1_RELOAD,
            H2_RELOAD,

            // ACTIONS
            PUSH_BUTTON,

            // STATUSES
            SHOCKED,
            STUNNED,

            // DEATH ANIMS
            DIE_BACKWARD,
            DIE_BACKWARD_HEAD,
            DIE_FORWARD,
            DIE_FORWARD_2,
            DIE_FORWARD_BACKSTAB,
            DIE_FORWARD_HEAD,
            DIE_BACKWARD_CROUCH,
            DIE_FORWARD_CROUCH,

            NONE
        }
    }
}
