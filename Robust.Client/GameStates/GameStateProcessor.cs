﻿using System.Collections.Generic;
using System.Linq;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Interfaces.Timing;
using Robust.Shared.Log;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Client.GameStates
{
    /// <inheritdoc />
    internal class GameStateProcessor : IGameStateProcessor
    {
        private readonly IGameTiming _timing;

        private readonly List<GameState> _stateBuffer = new List<GameState>();
        private GameState _lastFullState;
        private bool _waitingForFull = true;
        private int _interpRatio;
        private GameTick _lastProcessedRealState;
        private GameTick _highestFromSequence;

        private readonly Dictionary<EntityUid, Dictionary<uint, ComponentState>> _lastStateFullRep
            = new Dictionary<EntityUid, Dictionary<uint, ComponentState>>();

        /// <inheritdoc />
        public int MinBufferSize => Interpolation ? 3 : 2;

        /// <inheritdoc />
        public int TargetBufferSize => MinBufferSize + InterpRatio;

        /// <inheritdoc />
        public int CurrentBufferSize => CalculateBufferSize(_timing.CurTick);

        /// <inheritdoc />
        public bool Interpolation { get; set; }

        /// <inheritdoc />
        public int InterpRatio
        {
            get => _interpRatio;
            set => _interpRatio = value < 0 ? 0 : value;
        }

        /// <inheritdoc />
        public bool Extrapolation { get; set; }

        /// <inheritdoc />
        public bool Logging { get; set; }

        /// <summary>
        ///     Constructs a new instance of <see cref="GameStateProcessor"/>.
        /// </summary>
        /// <param name="timing">Timing information of the current state.</param>
        public GameStateProcessor(IGameTiming timing)
        {
            _timing = timing;
        }

        /// <inheritdoc />
        public void AddNewState(GameState state)
        {
            // any state from tick 0 is a full state, and needs to be handled different
            if (state.FromSequence == GameTick.Zero)
            {
                // this is a newer full state, so discard the older one.
                if (_lastFullState == null || (_lastFullState != null && _lastFullState.ToSequence < state.ToSequence))
                {
                    _lastFullState = state;

                    if (Logging)
                        Logger.InfoS("net", $"Received Full GameState: to={state.ToSequence}, sz={state.PayloadSize}");

                    return;
                }
            }

            // NOTE: DispatchTick will be modifying CurTick, this is NOT thread safe.
            var lastTick = new GameTick(_timing.CurTick.Value - 1);

            if (state.ToSequence <= lastTick && !_waitingForFull) // CurTick isn't set properly when WaitingForFull
            {
                if (Logging)
                    Logger.DebugS("net.state", $"Received Old GameState: cTick={_timing.CurTick}, fSeq={state.FromSequence}, tSeq={state.ToSequence}, sz={state.PayloadSize}, buf={_stateBuffer.Count}");

                return;
            }

            // lets check for a duplicate state now.
            for (var i = 0; i < _stateBuffer.Count; i++)
            {
                var iState = _stateBuffer[i];

                if (state.ToSequence != iState.ToSequence)
                    continue;

                if (Logging)
                    Logger.DebugS("net.state", $"Received Dupe GameState: cTick={_timing.CurTick}, fSeq={state.FromSequence}, tSeq={state.ToSequence}, sz={state.PayloadSize}, buf={_stateBuffer.Count}");

                return;
            }

            // this is a good state that we will be using.
            _stateBuffer.Add(state);

            if (Logging)
                Logger.DebugS("net.state", $"Received New GameState: cTick={_timing.CurTick}, fSeq={state.FromSequence}, tSeq={state.ToSequence}, sz={state.PayloadSize}, buf={_stateBuffer.Count}");
        }

        /// <inheritdoc />
        public bool ProcessTickStates(GameTick curTick, out GameState curState, out GameState nextState)
        {
            bool applyNextState;
            if (_waitingForFull)
            {
                applyNextState = CalculateFullState(out curState, out nextState, TargetBufferSize);
            }
            else // this will be how almost all states are calculated
            {
                applyNextState = CalculateDeltaState(curTick, out curState, out nextState);
            }

            if (applyNextState && !curState.Extrapolated)
                _lastProcessedRealState = curState.ToSequence;

            if (!_waitingForFull)
            {
                if (!applyNextState)
                    _timing.CurTick = _lastProcessedRealState;

                // This will slightly speed up or slow down the client tickrate based on the contents of the buffer.
                // CalcNextState should have just cleaned out any old states, so the buffer contains [t-1(last), t+0(cur), t+1(next), t+2, t+3, ..., t+n]
                // we can use this info to properly time our tickrate so it does not run fast or slow compared to the server.
                _timing.TickTimingAdjustment = (CurrentBufferSize - (float)TargetBufferSize) * 0.10f;
            }
            else
            {
                _timing.TickTimingAdjustment = 0f;
            }

            if (applyNextState)
            {
                if (Logging)
                {
                    Logger.DebugS("net.state", $"Applying State:  ext={curState.Extrapolated}, cTick={_timing.CurTick}, fSeq={curState.FromSequence}, tSeq={curState.ToSequence}, buf={_stateBuffer.Count}");
                }

                if (!curState.Extrapolated)
                {
                    UpdateFullRep(curState);
                }
            }

            return applyNextState;
        }

        private void UpdateFullRep(GameState state)
        {
            if (state.FromSequence == GameTick.Zero)
            {
                // Full state.
                _lastStateFullRep.Clear();
            }
            else
            {
                if (state.EntityDeletions != null)
                {
                    foreach (var deletion in state.EntityDeletions)
                    {
                        _lastStateFullRep.Remove(deletion);
                    }
                }
            }

            if (state.EntityStates != null)
            {
                foreach (var entityState in state.EntityStates)
                {
                    if (!_lastStateFullRep.TryGetValue(entityState.Uid, out var compData))
                    {
                        compData = new Dictionary<uint, ComponentState>();
                        _lastStateFullRep.Add(entityState.Uid, compData);
                    }

                    if (entityState.ComponentChanges != null)
                    {
                        foreach (var change in entityState.ComponentChanges)
                        {
                            if (change.Deleted)
                            {
                                compData.Remove(change.NetID);
                            }
                        }
                    }

                    if (entityState.ComponentStates != null)
                    {
                        foreach (var compState in entityState.ComponentStates)
                        {
                            compData[compState.NetID] = compState;
                        }
                    }
                }
            }
        }

        private bool CalculateFullState(out GameState curState, out GameState nextState, int targetBufferSize)
        {
            if (_lastFullState != null)
            {
                if (Logging)
                    Logger.DebugS("net", $"Resync CurTick to: {_lastFullState.ToSequence}");

                var curTick = _timing.CurTick = _lastFullState.ToSequence;

                if (Interpolation)
                {
                    // look for the next state
                    var lastTick = new GameTick(curTick.Value - 1);
                    var nextTick = new GameTick(curTick.Value + 1);
                    nextState = null;

                    for (var i = 0; i < _stateBuffer.Count; i++)
                    {
                        var state = _stateBuffer[i];
                        if (state.ToSequence == nextTick)
                        {
                            nextState = state;
                        }
                        else if (state.ToSequence < lastTick) // remove any old states we find to keep the buffer clean
                        {
                            _stateBuffer.RemoveSwap(i);
                            i--;
                        }
                    }

                    // we let the buffer fill up before starting to tick
                    if (nextState != null && _stateBuffer.Count >= targetBufferSize)
                    {
                        curState = _lastFullState;
                        _waitingForFull = false;
                        return true;
                    }
                }
                else if (_stateBuffer.Count >= targetBufferSize)
                {
                    curState = _lastFullState;
                    nextState = default;
                    _waitingForFull = false;
                    return true;
                }
            }

            if (Logging)
                Logger.DebugS("net", $"Have FullState, filling buffer... ({_stateBuffer.Count}/{targetBufferSize})");

            // waiting for full state or buffer to fill
            curState = default;
            nextState = default;
            return false;
        }

        private bool CalculateDeltaState(GameTick curTick, out GameState curState, out GameState nextState)
        {
            var lastTick = new GameTick(curTick.Value - 1);
            var nextTick = new GameTick(curTick.Value + 1);

            curState = null;
            nextState = null;

            GameState futureState = null;
            uint lastStateInput = 0;

            for (var i = 0; i < _stateBuffer.Count; i++)
            {
                var state = _stateBuffer[i];

                // remember there are no duplicate ToSequence states in the list.
                if (state.ToSequence == curTick)
                {
                    curState = state;
                    _highestFromSequence = state.FromSequence;
                }
                else if (Interpolation && state.ToSequence == nextTick)
                {
                    nextState = state;
                }
                else if (state.ToSequence > nextTick)
                {
                    futureState = state;
                }
                else if (state.ToSequence == lastTick)
                {
                    lastStateInput = state.LastProcessedInput;
                }
                else if (state.ToSequence < _highestFromSequence) // remove any old states we find to keep the buffer clean
                {
                    _stateBuffer.RemoveSwap(i);
                    i--;
                }
            }

            // can't find current state, but we do have a future state.
            if (!Extrapolation && curState == null && futureState != null)
            {
                //this is not actually extrapolation
                curState = ExtrapolateState(_highestFromSequence, curTick, lastStateInput);
                return true; // keep moving, we have a future state
            }

            // we won't extrapolate, and curState was not found, buffer is empty
            if (!Extrapolation && curState == null)
                return false;

            // we found both the states to interpolate between, this should almost always be true.
            if (Interpolation && curState != null)
                return true;

            if (!Interpolation && curState != null && nextState != null)
                return true;

            if (curState == null)
            {
                curState = ExtrapolateState(_highestFromSequence, curTick, lastStateInput);
            }

            if (nextState == null && Interpolation)
            {
                nextState = ExtrapolateState(_highestFromSequence, nextTick, lastStateInput);
            }

            return true;
        }

        /// <summary>
        ///     Generates a completely fake GameState.
        /// </summary>
        private static GameState ExtrapolateState(GameTick fromSequence, GameTick toSequence, uint lastInput)
        {
            var state = new GameState(fromSequence, toSequence, lastInput, null, null, null, null);
            state.Extrapolated = true;
            return state;
        }

        /// <inheritdoc />
        public void Reset()
        {
            _stateBuffer.Clear();
            _lastFullState = null;
            _waitingForFull = true;
        }

        public void MergeImplicitData(Dictionary<EntityUid, Dictionary<uint, ComponentState>> data)
        {
            foreach (var (uid, compData) in data)
            {
                var fullRep = _lastStateFullRep[uid];

                foreach (var (netId, compState) in compData)
                {
                    if (!fullRep.ContainsKey(netId))
                    {
                        fullRep.Add(netId, compState);
                    }
                }
            }
        }

        public Dictionary<uint, ComponentState> GetLastServerStates(EntityUid entity)
        {
            return _lastStateFullRep[entity];
        }

        public int CalculateBufferSize(GameTick fromTick)
        {
            return _stateBuffer.Count(s => s.ToSequence >= fromTick);
        }
    }
}
