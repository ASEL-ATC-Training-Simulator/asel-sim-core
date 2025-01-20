﻿using AviationCalcUtilNet.GeoTools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SaunaSim.Core.Simulator.Aircraft;
using SaunaSim.Core.Simulator.Aircraft.Autopilot;
using SaunaSim.Core.Simulator.Aircraft.Autopilot.Controller;
using AviationCalcUtilNet.Geo;

namespace SaunaSim.Core.Simulator.Commands
{
    public class TurnRightHeadingCommand : IAircraftCommand
    {
        public Action<string> Logger { get; set; }

        public SimAircraft Aircraft { get; set; }

        private int Hdg { get; set; }

        public void ExecuteCommand()
        {
            Aircraft.Autopilot.CurrentLateralMode = LateralModeType.HDG;
            Aircraft.Autopilot.SelectedHeading = Hdg;
            Aircraft.Autopilot.HdgKnobTurnDirection = McpKnobDirection.RIGHT;
        }
        public bool HandleCommand(SimAircraft aircraft, Action<string> logger, int hdg)
        {
            Aircraft = aircraft;
            Logger = logger;
            Hdg = hdg;
            Logger?.Invoke($"{Aircraft.Callsign} turning right heading {hdg} degrees.");

            // Check > 180 deg
            if ((Bearing.FromDegrees(Hdg) - Aircraft.Position.Heading_Mag).Value() < 0)
            {
                Logger?.Invoke($"WARNING: {Aircraft.Callsign} right turn exceeds 180 degrees!!");
            }
            return true;
        }

        public bool HandleCommand(ref List<string> args)
        {
            // Check argument length
            if (args.Count < 1)
            {
                Logger?.Invoke($"ERROR: Turn Right Heading requires at least 1 argument!");
                return false;
            }

            // Get heading string
            string headingString = args[0];
            args.RemoveAt(0);

            try
            {
                // Parse heading
                Hdg = Convert.ToInt32(headingString);

                Logger?.Invoke($"{Aircraft.Callsign} turning right heading {headingString} degrees.");

                // Check > 180 deg
                if ((Bearing.FromDegrees(Hdg) - Aircraft.Position.Heading_Mag).Value() < 0)
                {
                    Logger?.Invoke($"WARNING: {Aircraft.Callsign} right turn exceeds 180 degrees!!");
                }
            } catch (Exception)
            {
                Logger?.Invoke($"ERROR: Heading {headingString} not valid!");
                return false;
            }

            return true;
        }
    }
}
