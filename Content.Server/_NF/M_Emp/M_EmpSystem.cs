using System.Linq;
using Content.Server.Construction;
using Content.Server.GameTicking;
using Content.Server.Radio.EntitySystems;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Radio;
using Content.Shared._NF.M_Emp;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;
using Content.Server.Chat.Managers;
using Content.Server.Station.Systems;
using Robust.Shared.Timing;
using Content.Server.Emp;

namespace Content.Server._NF.M_Emp
{
    public sealed partial class M_EmpSystem : EntitySystem
    {
        [Dependency] private readonly IChatManager _chat = default!;
        [Dependency] private readonly IConfigurationManager _configurationManager = default!;
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
        [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
        [Dependency] private readonly RadioSystem _radioSystem = default!;
        [Dependency] private readonly SharedAppearanceSystem _appearanceSystem = default!;
        [Dependency] private readonly SharedAudioSystem _audio = default!;
        [Dependency] private readonly SharedTransformSystem _transform = default!;
        [Dependency] private readonly StationSystem _station = default!;
        [Dependency] private readonly UserInterfaceSystem _ui = default!;
        [Dependency] private readonly EmpSystem _emp = default!;

        // TODO: This is probably not compatible with multi-station
        private readonly Dictionary<EntityUid, M_EmpGridState> _M_EmpGridStates = new();

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<M_EmpGeneratorComponent, InteractHandEvent>(OnInteractHand);
            SubscribeLocalEvent<M_EmpGeneratorComponent, RefreshPartsEvent>(OnRefreshParts);
            SubscribeLocalEvent<M_EmpGeneratorComponent, UpgradeExamineEvent>(OnUpgradeExamine);
            SubscribeLocalEvent<M_EmpGeneratorComponent, ExaminedEvent>(OnExamined);
            SubscribeLocalEvent<M_EmpGeneratorComponent, ComponentShutdown>(OnGeneratorRemoval);
            SubscribeLocalEvent<GridRemovalEvent>(OnGridRemoval);

            // Can't use RoundRestartCleanupEvent, I need to clean up before the grid, and components are gone to prevent the announcements
            SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRoundEnd);
        }

        public override void Shutdown()
        {
            base.Shutdown();
        }

        private void OnRoundEnd(GameRunLevelChangedEvent ev)
        {
            if(ev.New != GameRunLevel.InRound)
            {
                _M_EmpGridStates.Clear();
            }
        }

        private void UpdateAppearance(EntityUid uid, M_EmpGeneratorComponent? component = null)
        {
            if (!Resolve(uid, ref component, false))
                return;

            _appearanceSystem.SetData(uid, M_EmpGeneratorVisuals.ReadyBlinking, component.GeneratorState.StateType == GeneratorStateType.Activating);
            _appearanceSystem.SetData(uid, M_EmpGeneratorVisuals.Ready, component.GeneratorState.StateType == GeneratorStateType.Engaged);
            _appearanceSystem.SetData(uid, M_EmpGeneratorVisuals.Unready, component.GeneratorState.StateType == GeneratorStateType.Recharging);
            _appearanceSystem.SetData(uid, M_EmpGeneratorVisuals.UnreadyBlinking, component.GeneratorState.StateType == GeneratorStateType.CoolingDown);
        }

        private void UpdateChargeStateAppearance(EntityUid uid, TimeSpan currentTime, M_EmpGeneratorComponent? component = null)
        {
            if (!Resolve(uid, ref component, false))
                return;

            var timeLeft = Convert.ToInt32(component.GeneratorState.Until.TotalSeconds - currentTime.TotalSeconds);

            component.ChargeRemaining = component.GeneratorState.StateType switch
            {
                GeneratorStateType.Inactive => 5,
                GeneratorStateType.Engaged => timeLeft / (Convert.ToInt32(component.EngagedTime.TotalSeconds) / component.ChargeCapacity) + 1,
                GeneratorStateType.CoolingDown => 0,
                GeneratorStateType.Recharging => component.ChargeCapacity - timeLeft / (Convert.ToInt32(component.CooldownTime.TotalSeconds) / component.ChargeCapacity) - 1,
                _ => component.ChargeRemaining
            };

            if (component.PreviousCharge == component.ChargeRemaining)
                return;
            _appearanceSystem.SetData(uid, M_EmpGeneratorVisuals.ChargeState, component.ChargeRemaining);
            component.PreviousCharge = component.ChargeRemaining;
        }

        private void OnGridRemoval(GridRemovalEvent ev)
        {
            // If we ever want to give generators names, and announce them individually, we would need to loop this, before removing it.
            if (_M_EmpGridStates.Remove(ev.EntityUid))
            {
                // For the very unlikely possibility that the M_Emp generator was on a M_Emp, we will not return here
            }
            foreach(var gridState in _M_EmpGridStates)
            {
                foreach(var generator in gridState.Value.ActiveGenerators)
                {
                    if (!TryComp<M_EmpGeneratorComponent>(generator, out var generatorComponent))
                        continue;

                    generatorComponent.GeneratorState = GeneratorState.Inactive;
                    return;
                }
            }
        }

        private void OnGeneratorRemoval(EntityUid uid, M_EmpGeneratorComponent component, ComponentShutdown args)
        {
            if (component.GeneratorState.StateType == GeneratorStateType.Inactive)
                return;

            var generatorTranform = Transform(uid);
            if (generatorTranform.GridUid is not { } gridId || !_M_EmpGridStates.TryGetValue(gridId, out var M_EmpGridState))
                return;

            component.GeneratorState = GeneratorState.Inactive;
        }

        private void OnRefreshParts(EntityUid uid, M_EmpGeneratorComponent component, RefreshPartsEvent args)
        {
            var rating = args.PartRatings[component.MachinePartDelay] - 1;
            var factor = MathF.Pow(component.PartRatingDelay, rating);
            component.ActivatingTime = component.BaseActivatingTime * factor;
            component.CooldownTime = component.BaseCooldownTime * factor;
        }

        private void OnUpgradeExamine(EntityUid uid, M_EmpGeneratorComponent component, UpgradeExamineEvent args)
        {
            args.AddPercentageUpgrade("m_emp-system-generator-delay-upgrade", (float) (component.CooldownTime / component.BaseCooldownTime));
        }

        private void OnExamined(EntityUid uid, M_EmpGeneratorComponent component, ExaminedEvent args)
        {
            if (!args.IsInDetailsRange)
                return;

            var gotGrid = false;
            var remainingTime = TimeSpan.Zero;

            if (Transform(uid).GridUid is { } gridId &&
                _M_EmpGridStates.TryGetValue(gridId, out var M_EmpGridState))
            {
                remainingTime = component.GeneratorState.Until - M_EmpGridState.CurrentTime;
                gotGrid = true;
            }
            else
            {
                Log.Warning("Failed to load M_Emp grid state, can't display remaining time");
            }

            switch (component.GeneratorState.StateType)
            {
                case GeneratorStateType.Inactive:
                    args.PushMarkup(Loc.GetString("m_emp-system-generator-examined-inactive"));
                    break;
                case GeneratorStateType.Activating:
                    args.PushMarkup(Loc.GetString("m_emp-system-generator-examined-starting"));
                    break;
                case GeneratorStateType.CoolingDown:
                    args.PushMarkup(Loc.GetString("m_emp-system-generator-examined-cooling-down"));
                    break;
                case GeneratorStateType.Recharging:
                    if (gotGrid)
                        args.PushMarkup(Loc.GetString("m_emp-system-generator-examined-recharging", ("timeLeft", Math.Ceiling(remainingTime.TotalSeconds))));
                    break;
                case GeneratorStateType.Engaged:
                    if (gotGrid)
                        args.PushMarkup(Loc.GetString("m_emp-system-generator-examined-active", ("timeLeft", Math.Ceiling(remainingTime.TotalSeconds))));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void OnInteractHand(EntityUid uid, M_EmpGeneratorComponent component, InteractHandEvent args)
        {
            if (args.Handled)
                return;
            args.Handled = true;
            StartGenerator(uid, component, args.User);
            UpdateAppearance(uid, component);
        }

        private void StartGenerator(EntityUid uid, M_EmpGeneratorComponent component, EntityUid user)
        {
            switch (component.GeneratorState.StateType)
            {
                case GeneratorStateType.Inactive:
                    ShowPopup(uid, "m_emp-system-report-activate-success", user);
                    var generatorTransform = Transform(uid);
                    var gridId = generatorTransform.GridUid ?? throw new InvalidOperationException("Generator had no grid associated");
                    if (!_M_EmpGridStates.TryGetValue(gridId, out var gridState))
                    {
                        gridState = new M_EmpGridState();
                        _M_EmpGridStates[gridId] = gridState;
                    }
                    gridState.ActiveGenerators.Add(uid);

                    PlayActivatedSound(uid, component);

                    component.GeneratorState = new GeneratorState(GeneratorStateType.Activating, gridState.CurrentTime + component.ActivatingTime);
                    RaiseLocalEvent(new M_EmpGeneratorActivatedEvent(uid));

                    var station = _station.GetOwningStation(uid);
                    var stationName = station is null ? null : Name(station.Value);

                    Report(uid, component.M_EmpChannel, "m_emp-system-report-activate-success", ("grid", stationName));

                    break;
                case GeneratorStateType.Activating:
                case GeneratorStateType.Engaged:
                    ShowPopup(uid, "m_emp-system-report-already-active", user);
                    break;
                case GeneratorStateType.CoolingDown:
                case GeneratorStateType.Recharging:
                    ShowPopup(uid, "m_emp-system-report-recharging", user);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        private void ShowPopup(EntityUid uid, string messageKey, EntityUid user)
        {
            _popupSystem.PopupEntity(Loc.GetString(messageKey), uid, user);
        }

        private bool SpawnM_Emp(EntityUid uid, M_EmpGeneratorComponent component)
        {
            Report(uid, component.M_EmpChannel, "m_emp-system-announcement-active", ("timeLeft", component.EngagedTime.TotalSeconds));

            var empRange = 100;
            var empEnergyConsumption = 50000;
            var empDisabledDuration = 60;

            _emp.EmpPulse(Transform(uid).MapPosition, empRange, empEnergyConsumption, empDisabledDuration);

            return true;
        }

        private void Report(EntityUid source, string channelName, string messageKey, params (string, object)[] args)
        {
            var message = args.Length == 0 ? Loc.GetString(messageKey) : Loc.GetString(messageKey, args);
            var channel = _prototypeManager.Index<RadioChannelPrototype>(channelName);
            _radioSystem.SendRadioMessage(source, message, channel, source);
        }

        private void Transition(EntityUid uid, M_EmpGeneratorComponent generator, TimeSpan currentTime)
        {
            switch (generator.GeneratorState.StateType)
            {
                case GeneratorStateType.Activating:
                    if (SpawnM_Emp(uid, generator))
                    {
                        generator.GeneratorState = new GeneratorState(GeneratorStateType.Engaged, currentTime + generator.EngagedTime);
                    }
                    else
                    {
                        generator.GeneratorState = new GeneratorState(GeneratorStateType.Recharging, currentTime + generator.CooldownTime);
                    }
                    break;
                case GeneratorStateType.Engaged:
                    Report(uid, generator.M_EmpChannel, "m_emp-system-announcement-cooling-down", ("timeLeft", generator.CoolingDownTime.TotalSeconds));
                    generator.GeneratorState = new GeneratorState(GeneratorStateType.CoolingDown, currentTime + generator.CoolingDownTime);
                    break;
                case GeneratorStateType.CoolingDown:
                    //Report(uid, generator.M_EmpChannel, "m_emp-system-announcement-recharging"); //Less chat spam
                    generator.GeneratorState = new GeneratorState(GeneratorStateType.Recharging, currentTime + generator.CooldownTime);
                    break;
                case GeneratorStateType.Recharging:
                    generator.GeneratorState = GeneratorState.Inactive;
                    break;
            }
            UpdateAppearance(uid, generator);
            UpdateChargeStateAppearance(uid, currentTime, generator);
        }

        public override void Update(float frameTime)
        {
            var secondsPassed = TimeSpan.FromSeconds(frameTime);
            // Keep track of time, and state per grid
            foreach (var (uid, state) in _M_EmpGridStates)
            {
                if (state.ActiveGenerators.Count == 0) continue;
                // Not handling the case where the M_Emp we spawned got paused
                // They both need to be paused, or it doesn't make sense
                if (MetaData(uid).EntityPaused) continue;
                state.CurrentTime += secondsPassed;

                var deleteQueue = new RemQueue<EntityUid>();

                foreach(var generator in state.ActiveGenerators)
                {
                    if (!TryComp<M_EmpGeneratorComponent>(generator, out var generatorComp))
                        continue;

                    UpdateChargeStateAppearance(generator, state.CurrentTime, generatorComp);
                    if (generatorComp.GeneratorState.Until > state.CurrentTime) continue;
                    Transition(generator, generatorComp, state.CurrentTime);
                    if (generatorComp.GeneratorState.StateType == GeneratorStateType.Inactive)
                    {
                        deleteQueue.Add(generator);
                    }
                }

                foreach(var generator in deleteQueue)
                {
                    state.ActiveGenerators.Remove(generator);
                }
            }
        }
        private void PlayActivatedSound(EntityUid uid, SharedM_EmpGeneratorComponent component)
        {
            _audio.PlayPvs(_audio.GetSound(component.ActivatedSound), uid);
        }
    }

    public sealed class M_EmpGridState
    {
        public TimeSpan CurrentTime { get; set; }
        public List<EntityUid> ActiveGenerators { get; } = new();
    }
}

