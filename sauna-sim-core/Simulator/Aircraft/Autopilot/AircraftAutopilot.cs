﻿using System;
using System.Collections.Generic;
using SaunaSim.Core.Simulator.Aircraft.Autopilot.Controller;
using SaunaSim.Core.Simulator.Aircraft.FMS;
using SaunaSim.Core.Simulator.Aircraft.FMS.Legs;
using SaunaSim.Core.Simulator.Aircraft.Performance;
using System.Net;
using AviationCalcUtilNet.Units;
using AviationCalcUtilNet.Geo;
using AviationCalcUtilNet.Aviation;
using AviationCalcUtilNet.Atmos.Grib;
using AviationCalcUtilNet.Atmos;
using SaunaSim.Core.Data;
using FsdConnectorNet.Args;

namespace SaunaSim.Core.Simulator.Aircraft.Autopilot
{
    public class AircraftAutopilot
    {
        private readonly SimAircraft _parentAircraft;
        
        // Autopilot internal variables
        private double _targetThrust;
        private Angle _targetBank;
        private Angle _targetPitch;

        // Modes
        private LateralModeType _curLatMode;
        private readonly List<LateralModeType> _armedLatModes;
        private readonly object _armedLatModesLock = new object();
        private VerticalModeType _curVertMode;
        private readonly List<VerticalModeType> _armedVertModes;
        private readonly object _armedVertModesLock = new object();
        private ThrustModeType _curThrustMode;
        private readonly List<ThrustModeType> _armedThrustModes;
        private readonly object _armedThrustModesLock = new object();

        // MCP
        private McpKnobDirection _hdgKnobDir;
        private int _selHdg;
        private int _selAlt;
        private int _selVs;
        private double _selFpa;
        private McpSpeedSelectorType _spdMode;
        private McpSpeedUnitsType _spdUnits;
        private int _selSpd;

        public AircraftAutopilot(SimAircraft parentAircraft)
        {
            _parentAircraft = parentAircraft;

            // Initialize Modes
            _curLatMode = LateralModeType.BANK;
            _curThrustMode = ThrustModeType.SPEED;
            _curVertMode = VerticalModeType.FPA;
            _armedLatModes = new List<LateralModeType>();
            _armedVertModes = new List<VerticalModeType>();
            _armedThrustModes = new List<ThrustModeType>();

            // Initialize MCP
            _selHdg = 0;
            _hdgKnobDir = McpKnobDirection.SHORTEST;
            _selAlt = 0;
            _selVs = 0;
            _selFpa = 0;
            _spdMode = McpSpeedSelectorType.FMS;
            _spdUnits = McpSpeedUnitsType.KNOTS;
            _selSpd = 250;
        }

        public void OnPositionUpdate(int intervalMs)
        {
            // Check for FMS Speed
            if (_spdMode == McpSpeedSelectorType.FMS)
            {
                _spdUnits = _parentAircraft.Fms.FmsSpeedUnits;
                _selSpd = _parentAircraft.Fms.FmsSpeedValue;
            }

            
            // Run Bank Controller
            RunRollController(intervalMs);

            // Run Pitch Controller
            RunPitchController(intervalMs);

            // Run Thrust Controller
            RunThrustController(intervalMs);
        }

        private void RunThrustController(int intervalMs)
        {
            if (_curThrustMode == ThrustModeType.SPEED)
            {
                // Convert selected mach # to IAS if required
                Velocity selSpdKts = Velocity.FromKnots(_selSpd);
                if (_spdUnits == McpSpeedUnitsType.MACH)
                {
                    selSpdKts = ConvertMachToKts(_selSpd);
                }

                // Calculate required thrust
                Velocity speedDelta = _parentAircraft.Position.IndicatedAirSpeed - selSpdKts;
                double zeroAccelThrust = PerfDataHandler.GetRequiredThrustForVs(_parentAircraft.PerformanceData, _parentAircraft.Position.VerticalSpeed.FeetPerMinute, 0,
                    selSpdKts.Knots, _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg,
                    _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config);
                _targetThrust = AutopilotUtil.CalculateDemandedThrottleForSpeed(
                    speedDelta,
                    _parentAircraft.Data.ThrustLeverPos,
                    zeroAccelThrust * 100.0,
                    (thrust) =>
                        Acceleration.FromKnotsPerSecond(PerfDataHandler.CalculatePerformance(_parentAircraft.PerformanceData, _parentAircraft.Position.Pitch.Degrees, thrust / 100.0,
                            _parentAircraft.Position.IndicatedAirSpeed.Knots, _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg, _parentAircraft.Data.SpeedBrakePos,
                            _parentAircraft.Data.Config).accelFwd),
                    intervalMs
                );
                _parentAircraft.Data.ThrustLeverVel = AutopilotUtil.CalculateThrustRate(_targetThrust, _parentAircraft.Data.ThrustLeverPos, intervalMs);
            } else if (_curThrustMode == ThrustModeType.THRUST)
            {
                _parentAircraft.Data.ThrustLeverVel = AutopilotUtil.CalculateThrustRate(_targetThrust, _parentAircraft.Data.ThrustLeverPos, intervalMs);
            }
        }

        private void RunPitchController(int intervalMs)
        {
            // Check armed vertical modes
            lock (_armedVertModesLock)
            {
                foreach (var mode in _armedVertModes)
                {
                    // TODO: Needs to be changed to only activate when necessary. OK for now
                    // Check if APCH mode should be activated
                    if (mode == VerticalModeType.APCH && CurrentLateralMode == LateralModeType.APCH && ShouldCaptureVnavPath(intervalMs))
                    {
                        RemoveArmedVerticalMode(mode);
                        _curVertMode = mode;
                        break;
                    }
                }
            }

            if (_curVertMode == VerticalModeType.ALT || _curVertMode == VerticalModeType.VALT)
            {
                if (_curLatMode != LateralModeType.LNAV)
                {
                    _curVertMode = VerticalModeType.ALT;
                }
                // Check if we're off altitude by more than 200ft
                Length altDelta = _parentAircraft.Position.IndicatedAltitude - Length.FromFeet(_selAlt);
                if (Math.Abs(altDelta.Feet) > 200)
                {
                    PitchHandleFlch(intervalMs);
                } else
                {
                    PitchHandleAsel(intervalMs);
                }
            } else if (_curVertMode == VerticalModeType.FLCH)
            {
                if (PitchShouldAsel(intervalMs))
                {
                    _curVertMode = VerticalModeType.ASEL;
                    PitchHandleAsel(intervalMs);
                } else
                {
                    PitchHandleFlch(intervalMs);
                }
            } else if (_curVertMode == VerticalModeType.ASEL || _curVertMode == VerticalModeType.VASEL)
            {
                if (_curLatMode != LateralModeType.LNAV)
                {
                    _curVertMode = VerticalModeType.ASEL;
                }

                // Check if we're at altitude
                Length altDelta = _parentAircraft.Position.IndicatedAltitude - Length.FromFeet(_selAlt);
                if (Math.Abs(_parentAircraft.Position.VerticalSpeed.FeetPerMinute) < 50 && Math.Abs(altDelta.Feet) < 1)
                {
                    _curVertMode = _curVertMode == VerticalModeType.ASEL ? VerticalModeType.ALT : VerticalModeType.VALT;
                }

                PitchHandleAsel(intervalMs);
            } else if (_curVertMode == VerticalModeType.VS)
            {
                if (PitchShouldAsel(intervalMs))
                {
                    _curVertMode = VerticalModeType.ASEL;
                    PitchHandleAsel(intervalMs);
                } else
                {
                    // Set thrust mode to speed
                    _curThrustMode = ThrustModeType.SPEED;

                    // Calculate pitch and pitch rate
                    _targetPitch = Angle.FromDegrees(PerfDataHandler.GetRequiredPitchForVs(_parentAircraft.PerformanceData, _selVs,
                        _parentAircraft.Position.IndicatedAirSpeed.Knots, _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg,
                        _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config));
                    _parentAircraft.Position.PitchRate = AutopilotUtil.CalculatePitchRate(_targetPitch, _parentAircraft.Position.Pitch, PitchToVsFunction, intervalMs);
                }
            } else if (_curVertMode == VerticalModeType.FPA)
            {
                if (PitchShouldAsel(intervalMs))
                {
                    _curVertMode = VerticalModeType.ASEL;
                    PitchHandleAsel(intervalMs);
                } else
                {
                    // Set thrust mode to speed
                    _curThrustMode = ThrustModeType.SPEED;
                }
            } else if (_curVertMode == VerticalModeType.APCH)
            {
                if (_curLatMode != LateralModeType.APCH)
                {
                    _curVertMode = VerticalModeType.FPA;
                    _selFpa = _parentAircraft.Position.FlightPathAngle.Degrees;
                    PitchHandleFpa(intervalMs);
                } else if (_parentAircraft.Position.TrueAltitude < _parentAircraft.RelaventAirport.Elevation + Length.FromFeet(30))
                {
                    _curVertMode = VerticalModeType.LAND;
                    PitchHandleLand(intervalMs);
                }
                else
                {
                    _curThrustMode = ThrustModeType.SPEED;

                    PitchHandleVnav(intervalMs);
                }

            } else if (_curVertMode == VerticalModeType.VPTH)
            {
                if (_curLatMode != LateralModeType.LNAV)
                {
                    _curVertMode = VerticalModeType.FPA;
                    _selFpa = _parentAircraft.Position.FlightPathAngle.Degrees;
                    PitchHandleFpa(intervalMs);
                } else if (PitchShouldAsel(intervalMs))
                {
                    _curVertMode = VerticalModeType.VASEL;
                    PitchHandleAsel(intervalMs);
                } else
                {
                    // Set thrust mode to speed
                    _curThrustMode = ThrustModeType.SPEED;

                    PitchHandleVnav(intervalMs);
                }
            }else if(_curVertMode == VerticalModeType.LAND)
            {
                PitchHandleLand(intervalMs);
            }
        }

        private void RunRollController(int intervalMs)
        {
            // Check armed lateral modes
            lock (_armedLatModesLock)
            {
                foreach (var mode in _armedLatModes)
                {
                    if ((mode == LateralModeType.LNAV || mode == LateralModeType.APCH) && _parentAircraft.Fms.ShouldActivateLnav(intervalMs))
                    {
                        RemoveArmedLateralMode(mode);
                        _curLatMode = mode;
                        break;
                    }
                }
            }

            // Handle Active Mode
            if (_curLatMode == LateralModeType.BANK)
            {
                _parentAircraft.Position.BankRate = AutopilotUtil.CalculateRollRate(_targetBank, _parentAircraft.Position.Bank, intervalMs);
            } else if (_curLatMode == LateralModeType.HDG)
            {
                RollHdgTrackHold(_parentAircraft.Position.Heading_Mag,SelectedHeadingBearing, intervalMs);
            } else if (_curLatMode == LateralModeType.TRACK)
            {
                RollHdgTrackHold(_parentAircraft.Position.Track_Mag, SelectedHeadingBearing, intervalMs);
            } else if (_curLatMode == LateralModeType.LNAV || _curLatMode == LateralModeType.APCH)
            {
                RollHandleLnav(intervalMs);
            }
        }

        private void RollHandleLnav(int intervalMs)
        {
            var fms = _parentAircraft.Fms;

            if (fms.ActiveLeg == null)
            {
                _selHdg = (int)_parentAircraft.Position.Track_Mag;
                _curLatMode = LateralModeType.TRACK;
                return;
            }

            // Get True Course and crossTrackError
            (Bearing requiredTrueCourse, Length crossTrackError, _, Length turnRadius) = fms.CourseInterceptInfo;

            // Calculate Bank Angle & Rate
            _targetBank = AutopilotUtil.CalculateDemandedRollForNav(crossTrackError, _parentAircraft.Position.Track_True, requiredTrueCourse,
                turnRadius, _parentAircraft.Position.Bank, _parentAircraft.Position.GroundSpeed, intervalMs).demandedRoll;

            _parentAircraft.Position.BankRate = AutopilotUtil.CalculateRollRate(_targetBank, _parentAircraft.Position.Bank, intervalMs);
        }

        private void RollHdgTrackHold(Bearing currentBearing, Bearing targetBearing, int intervalMs)
        {
            Angle hdgDelta = targetBearing - currentBearing;

            // Go the shortest way if we're almost at the heading
            if (Math.Abs(hdgDelta.Degrees) < 1)
            {
                _hdgKnobDir = McpKnobDirection.SHORTEST;
            }

            if (Math.Abs(hdgDelta.Radians) > double.Epsilon || Math.Abs(_parentAircraft.Position.Bank.Radians) > double.Epsilon)
            {
                // Figure out turn direction
                bool isRightTurn = (_hdgKnobDir == McpKnobDirection.SHORTEST) ? (hdgDelta.Radians > 0) : (_hdgKnobDir == McpKnobDirection.RIGHT);

                // Get new hdgDelta
                if (isRightTurn && hdgDelta.Radians < 0)
                {
                    hdgDelta += Angle.FromDegrees(360);
                } else if (!isRightTurn && hdgDelta.Radians > 0)
                {
                    hdgDelta -= Angle.FromDegrees(360);
                }

                // Desired bank angle
                _targetBank = AutopilotUtil.CalculateDemandedRollForTurn(hdgDelta, _parentAircraft.Position.Bank, _parentAircraft.Position.GroundSpeed, intervalMs);
                _parentAircraft.Position.BankRate = AutopilotUtil.CalculateRollRate(_targetBank, _parentAircraft.Position.Bank, intervalMs);
            } else
            {
                _parentAircraft.Position.BankRate = (AngularVelocity)0;
            }
        }

        private Velocity PitchToVsFunction(Angle pitch)
        {
            return Velocity.FromFeetPerMinute(PerfDataHandler.CalculatePerformance(
                _parentAircraft.PerformanceData,
                pitch.Degrees,
                _parentAircraft.Data.ThrustLeverPos / 100.0, 
                _parentAircraft.Position.IndicatedAirSpeed.Knots,
                _parentAircraft.Position.DensityAltitude.Feet, 
                _parentAircraft.Data.Mass_kg, 
                _parentAircraft.Data.SpeedBrakePos, 
                _parentAircraft.Data.Config).vs);
        }

        private bool PitchShouldAsel(int intervalMs)
        {
            Length altDelta = _parentAircraft.Position.IndicatedAltitude - Length.FromFeet(_selAlt);

            // Calculate required ASEL pitch
            double zeroVsPitch = PerfDataHandler.GetRequiredPitchForVs(_parentAircraft.PerformanceData, 0,
                _parentAircraft.Position.IndicatedAirSpeed.Knots, _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg,
                _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config);
            double idlePitch = PerfDataHandler.GetRequiredPitchForThrust(_parentAircraft.PerformanceData, 0, 0, _parentAircraft.Position.IndicatedAirSpeed.Knots,
                _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg, _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config);
            double maxPitch = PerfDataHandler.GetRequiredPitchForThrust(_parentAircraft.PerformanceData, 1, 0, _parentAircraft.Position.IndicatedAirSpeed.Knots,
                _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg, _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config);
            Angle pitchTarget = AutopilotUtil.CalculateDemandedPitchForAltitude(
                altDelta,
                _parentAircraft.Position.Pitch,
                Angle.FromDegrees(zeroVsPitch),
                 Angle.FromDegrees(idlePitch),
                Angle.FromDegrees(maxPitch),
                (pitch) =>
                    Velocity.FromFeetPerMinute(PerfDataHandler.CalculatePerformance(_parentAircraft.PerformanceData, pitch.Degrees, _parentAircraft.Data.ThrustLeverPos / 100.0,
                        _parentAircraft.Position.IndicatedAirSpeed.Knots, _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg, _parentAircraft.Data.SpeedBrakePos,
                        _parentAircraft.Data.Config).vs),
                intervalMs
            );

            return Math.Abs(zeroVsPitch - pitchTarget.Degrees) < double.Epsilon;
        }

        private void PitchHandleLand(int intervalMs)
        {
            Length altDelta = _parentAircraft.Position.IndicatedAltitude - _parentAircraft.RelaventAirport.Elevation.Feet;
            _curThrustMode = ThrustModeType.THRUST;
            _targetThrust = 0;

            _targetPitch = Angle.FromDegrees(PerfDataHandler.GetRequiredPitchForVs(_parentAircraft.PerformanceData, -150,
                        _parentAircraft.Position.IndicatedAirSpeed.Knots, _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg,
                        _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config));
            _parentAircraft.Position.PitchRate = AutopilotUtil.CalculatePitchRate(_targetPitch, _parentAircraft.Position.Pitch, PitchToVsFunction, intervalMs);
        }

        private void PitchHandleAsel(int intervalMs)
        {
            Length altDelta = _parentAircraft.Position.IndicatedAltitude - SelectedAltitudeLength;
            _curThrustMode = ThrustModeType.SPEED;

            // Calculate required ASEL pitch
            double zeroVsPitch = PerfDataHandler.GetRequiredPitchForVs(_parentAircraft.PerformanceData, 0,
                _parentAircraft.Position.IndicatedAirSpeed.Knots, _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg,
                _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config);
            double idlePitch = PerfDataHandler.GetRequiredPitchForThrust(_parentAircraft.PerformanceData, 0, 0, _parentAircraft.Position.IndicatedAirSpeed.Knots,
                _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg, _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config);
            double maxPitch = PerfDataHandler.GetRequiredPitchForThrust(_parentAircraft.PerformanceData, 1, 0, _parentAircraft.Position.IndicatedAirSpeed.Knots,
                _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg, _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config);
            _targetPitch = AutopilotUtil.CalculateDemandedPitchForAltitude(
                altDelta,
                _parentAircraft.Position.Pitch,
                Angle.FromDegrees(zeroVsPitch),
                Angle.FromDegrees(idlePitch),
                Angle.FromDegrees(maxPitch),
                (pitch) =>
                    Velocity.FromFeetPerMinute(PerfDataHandler.CalculatePerformance(_parentAircraft.PerformanceData, pitch.Degrees, _parentAircraft.Data.ThrustLeverPos / 100.0,
                        _parentAircraft.Position.IndicatedAirSpeed.Knots, _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg, _parentAircraft.Data.SpeedBrakePos,
                        _parentAircraft.Data.Config).vs),
                intervalMs
            );

            _parentAircraft.Position.PitchRate = AutopilotUtil.CalculatePitchRate(_targetPitch, _parentAircraft.Position.Pitch, PitchToVsFunction, intervalMs);
        }

        private void PitchHandleFpa(int intervalMs)
        {
            // Calculate pitch and pitch rate
            _targetPitch = Angle.FromDegrees(PerfDataHandler.GetRequiredPitchForVs(_parentAircraft.PerformanceData, AviationUtil.CalculateVerticalSpeed(_parentAircraft.Position.GroundSpeed, SelectedFpaAngle).FeetPerMinute,
                _parentAircraft.Position.IndicatedAirSpeed.Knots, _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg,
                _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config));
            _parentAircraft.Position.PitchRate = AutopilotUtil.CalculatePitchRate(_targetPitch, _parentAircraft.Position.Pitch, PitchToVsFunction, intervalMs);
        }

        private void PitchHandleFlch(int intervalMs)
        {
            // Figure out thrust setting
            Length altDelta = _parentAircraft.Position.IndicatedAltitude - SelectedAltitudeLength;
            _curThrustMode = ThrustModeType.THRUST;

            if (altDelta.Meters > 0 && _targetThrust > double.Epsilon)
            {
                _targetThrust = 0;
            } else if (altDelta.Meters < 0 && _targetThrust < 100)
            {
                _targetThrust = 100;
            }

            // Convert selected mach # to IAS if required
            double selSpdKts = _selSpd;
            if (_spdUnits == McpSpeedUnitsType.MACH)
            {
                selSpdKts = ConvertMachToKts(_selSpd).Knots;
            }

            // Calculate pitch limits
            Angle maxPitch = AutopilotUtil.PITCH_LIMIT_MAX;
            Angle minPitch = AutopilotUtil.PITCH_LIMIT_MIN;
            Angle zeroVsPitch = Angle.FromDegrees(PerfDataHandler.GetRequiredPitchForVs(_parentAircraft.PerformanceData, 0,
                _parentAircraft.Position.IndicatedAirSpeed.Knots, _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg,
                _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config));
            if (altDelta.Meters > 0)
            {
                maxPitch = zeroVsPitch;
            } else
            {
                minPitch = zeroVsPitch;
            }

            // Calculate required pitch
            Velocity speedDelta = _parentAircraft.Position.IndicatedAirSpeed - Velocity.FromKnots(selSpdKts);
            double zeroAccelPitch = PerfDataHandler.GetRequiredPitchForThrust(_parentAircraft.PerformanceData, _parentAircraft.Data.ThrustLeverPos / 100.0, 0,
                selSpdKts, _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg,
                _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config);
            _targetPitch = AutopilotUtil.CalculateDemandedPitchForSpeed(
                speedDelta,
                _parentAircraft.Position.Pitch,
                Angle.FromDegrees(zeroAccelPitch),
                maxPitch,
                minPitch,
                (pitch) =>
                    Acceleration.FromKnotsPerSecond(PerfDataHandler.CalculatePerformance(_parentAircraft.PerformanceData, pitch.Degrees, _parentAircraft.Data.ThrustLeverPos / 100.0,
                        _parentAircraft.Position.IndicatedAirSpeed.Knots, _parentAircraft.Position.DensityAltitude.Feet, _parentAircraft.Data.Mass_kg, _parentAircraft.Data.SpeedBrakePos,
                        _parentAircraft.Data.Config).accelFwd),
                PitchToVsFunction,
                intervalMs
            );
            _parentAircraft.Position.PitchRate = AutopilotUtil.CalculatePitchRate(_targetPitch, _parentAircraft.Position.Pitch, PitchToVsFunction, intervalMs);
        }

        private bool ShouldCaptureVnavPath(int intervalMs)
        {
            var fms = _parentAircraft.Fms;

            if (fms.ActiveLeg == null)
            {
                return false;
            }

            // Get Required FPA and Vertical Deviation
            (Angle reqFpa, Length vTk_m) = (fms.RequiredFpa, fms.VerticalTrackDistance);

            // Calculate target FPA
            Angle targetFpa = AutopilotUtil.CalculateDemandedPitchForVnav(vTk_m, _parentAircraft.Position.FlightPathAngle, -reqFpa, _parentAircraft.PerformanceData, _parentAircraft.Position.IndicatedAirSpeed, _parentAircraft.Position.DensityAltitude, _parentAircraft.Data.Mass_kg, _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config, _parentAircraft.Position.GroundSpeed, _parentAircraft.Data.ThrustLeverPos / 100.0, intervalMs).demandedFpa;

            if ((-reqFpa).Radians < 0)
            {
                return targetFpa.Degrees - 0.1 < -reqFpa.Degrees && -reqFpa.Degrees < targetFpa.Degrees + 0.1;
            }
            return false;
        }

        private void PitchHandleVnav(int intervalMs)
        {
            var fms = _parentAircraft.Fms;

            if (fms.ActiveLeg == null)
            {
                SelectedFpaAngle = _parentAircraft.Position.FlightPathAngle;
                _curVertMode = VerticalModeType.FPA;
                return;
            }

            // Get Required FPA and Vertical Deviation
            (Angle reqFpa, Length vTk_m) = (fms.RequiredFpa, fms.VerticalTrackDistance);

            // Calculate pitch and pitch rate
            _targetPitch = AutopilotUtil.CalculateDemandedPitchForVnav(vTk_m, _parentAircraft.Position.FlightPathAngle, -reqFpa, _parentAircraft.PerformanceData, _parentAircraft.Position.IndicatedAirSpeed, _parentAircraft.Position.DensityAltitude, _parentAircraft.Data.Mass_kg, _parentAircraft.Data.SpeedBrakePos, _parentAircraft.Data.Config, _parentAircraft.Position.GroundSpeed, _parentAircraft.Data.ThrustLeverPos / 100.0,  intervalMs).demandedPitch;
            _parentAircraft.Position.PitchRate = AutopilotUtil.CalculatePitchRate(_targetPitch, _parentAircraft.Position.Pitch, PitchToVsFunction, intervalMs);
        }

        private Velocity ConvertMachToKts(double speedMach)
        {
            GribDataPoint gribPoint = _parentAircraft.Position.GribPoint;
            Length trueAlt = _parentAircraft.Position.TrueAltitude;
            Pressure refPres = gribPoint?.LevelPressure ?? AtmosUtil.ISA_STD_PRES;
            Length refAlt = gribPoint?.GeoPotentialHeight ?? Length.FromFeet(0);
            Temperature refTemp = gribPoint?.Temp ?? AtmosUtil.ISA_STD_TEMP;
            Temperature T = AtmosUtil.CalculateTempAtAlt(trueAlt, refAlt, refTemp);
            Velocity selTas = AtmosUtil.ConvertMachToTas(speedMach / 100.0, T);
            return AtmosUtil.ConvertTasToIas(selTas, refPres, trueAlt, refAlt, refTemp).ias;
        }


        public void AddArmedLateralMode(LateralModeType mode)
        {
            lock (_armedLatModesLock)
            {
                if (_curLatMode != mode && !_armedLatModes.Contains(mode))
                {
                    _armedLatModes.Add(mode);
                }
            }
        }

        public void RemoveArmedLateralMode(LateralModeType mode)
        {
            lock (_armedLatModesLock)
            {
                _armedLatModes.Remove(mode);
            }
        }

        public void ClearArmedLateralModes()
        {
            lock (_armedLatModesLock)
            {
                _armedLatModes.Clear();
            }
        }

        public void AddArmedVerticalMode(VerticalModeType mode)
        {
            lock (_armedVertModesLock)
            {
                if (_curVertMode != mode && !_armedVertModes.Contains(mode))
                {
                    _armedVertModes.Add(mode);
                }
            }
        }

        public void RemoveArmedVerticalMode(VerticalModeType mode)
        {
            lock (_armedVertModesLock)
            {
                _armedVertModes.Remove(mode);
            }
        }

        public void ClearArmedVerticalModes()
        {
            lock (_armedVertModesLock)
            {
                _armedVertModes.Clear();
            }
        }

        public void AddArmedThrustMode(ThrustModeType mode)
        {
            lock (_armedThrustModesLock)
            {
                if (_curThrustMode != mode && !_armedThrustModes.Contains(mode))
                {
                    _armedThrustModes.Add(mode);
                }
            }
        }

        public void RemoveArmedThrustMode(ThrustModeType mode)
        {
            lock (_armedThrustModesLock)
            {
                _armedThrustModes.Remove(mode);
            }
        }

        public void ClearArmedThrustModes()
        {
            lock (_armedThrustModesLock)
            {
                _armedThrustModes.Clear();
            }
        }

        public int SelectedHeading
        {
            get => _selHdg;
            set
            {
                if (value < 0 || value >= 360)
                {
                    _selHdg = 0;
                } else
                {
                    _selHdg = value;
                }
            }
        }

        public Bearing SelectedHeadingBearing
        {
            get => Bearing.FromDegrees(SelectedHeading);
            set => SelectedHeading = (int) Math.Round(value.Degrees);
        }

        public int SelectedAltitude
        {
            get => _selAlt;
            set
            {
                if (value < 0)
                {
                    _selAlt = 0;
                } else if (value > 99999)
                {
                    _selAlt = 99999;
                } else
                {
                    _selAlt = value;
                }
            }
        }

        public Length SelectedAltitudeLength
        {
            get => Length.FromFeet(SelectedAltitude);
            set => SelectedAltitude = (int)Math.Round(value.Feet);
        }

        public int SelectedVerticalSpeed
        {
            get => _selVs;
            set => _selVs = value;
        }

        public Velocity SelectedVerticalSpeedVelocity
        {
            get => Velocity.FromFeetPerMinute(SelectedVerticalSpeed);
            set => SelectedVerticalSpeed = (int)Math.Round(value.FeetPerMinute);
        }

        public double SelectedFpa
        {
            get => _selFpa;
            set => _selFpa = value;
        }

        public Angle SelectedFpaAngle
        {
            get => Angle.FromDegrees(SelectedFpa);
            set => SelectedFpa = (int)Math.Round(value.Degrees);
        }

        public McpSpeedSelectorType SelectedSpeedMode
        {
            get => _spdMode;
            set => _spdMode = value;
        }

        public McpSpeedUnitsType SelectedSpeedUnits
        {
            get => _spdUnits;
            set
            {
                GribDataPoint gribPoint = _parentAircraft.Position.GribPoint;
                Length trueAlt = _parentAircraft.Position.TrueAltitude;
                Pressure refPres = gribPoint?.LevelPressure ?? AtmosUtil.ISA_STD_PRES;
                Length refAlt = gribPoint?.GeoPotentialHeight ?? Length.FromFeet(0);
                Temperature refTemp = gribPoint?.Temp ?? AtmosUtil.ISA_STD_TEMP;

                if (value == McpSpeedUnitsType.MACH && _spdUnits == McpSpeedUnitsType.KNOTS)
                {
                    // Convert selected speeds from IAS to Mach
                    double maxMach = AtmosUtil.ConvertIasToTas(Velocity.FromKnots(_selSpd), refPres, trueAlt, refAlt, refTemp).mach;
                    _selSpd = (int)(maxMach * 100);
                } else if (value == McpSpeedUnitsType.KNOTS && _spdUnits == McpSpeedUnitsType.MACH)
                {
                    // Convert selected speeds from Mach to IAS
                    Temperature T = AtmosUtil.CalculateTempAtAlt(trueAlt, refAlt, refTemp);
                    Velocity selTas = AtmosUtil.ConvertMachToTas(_selSpd / 100.0, T);
                    _selSpd = (int)AtmosUtil.ConvertTasToIas(selTas, refPres, trueAlt, refAlt, refTemp).ias.Knots;
                }

                _spdUnits = value;
            }
        }

        public int SelectedSpeed
        {
            get => _selSpd;
            set => _selSpd = value;
        }

        public LateralModeType CurrentLateralMode
        {
            get => _curLatMode;
            set => _curLatMode = value;
        }

        public List<LateralModeType> ArmedLateralModes
        {
            get
            {
                lock (_armedLatModesLock)
                {
                    return new List<LateralModeType>(_armedLatModes);
                }
            }
        }

        public VerticalModeType CurrentVerticalMode
        {
            get => _curVertMode;
            set => _curVertMode = value;
        }

        public List<VerticalModeType> ArmedVerticalModes
        {
            get
            {
                lock (_armedVertModesLock)
                {
                    return new List<VerticalModeType>(_armedVertModes);
                }
            }
        }

        public ThrustModeType CurrentThrustMode
        {
            get => _curThrustMode;
            set => _curThrustMode = value;
        }

        public List<ThrustModeType> ArmedThrustModes
        {
            get
            {
                lock (_armedThrustModesLock)
                {
                    return new List<ThrustModeType>(_armedThrustModes);
                }
            }
        }

        public McpKnobDirection HdgKnobTurnDirection
        {
            get => _hdgKnobDir;
            set => _hdgKnobDir = value;
        }
    }
}