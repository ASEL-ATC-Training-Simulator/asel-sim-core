﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AviationCalcUtilNet.Atmos.Grib;
using AviationCalcUtilNet.Geo;
using AviationCalcUtilNet.GeoTools;
using AviationCalcUtilNet.Magnetic;
using AviationCalcUtilNet.Units;
using FsdConnectorNet;
using NavData_Interface.Objects;
using NavData_Interface.Objects.Fixes;
using NavData_Interface.Objects.LegCollections.Airways;
using NavData_Interface.Objects.LegCollections.Legs;
using NavData_Interface.Objects.LegCollections.Procedures;
using SaunaSim.Core.Simulator.Aircraft;
using SaunaSim.Core.Simulator.Aircraft.Autopilot.Controller;
using SaunaSim.Core.Simulator.Aircraft.FMS;
using SaunaSim.Core.Simulator.Aircraft.FMS.Legs;
using SaunaSim.Core.Simulator.Aircraft.Performance;
using SaunaSim.Core.Simulator.Commands;

namespace SaunaSim.Core.Data.Loaders
{
    public class AircraftBuilder
    {
        public MagneticTileManager MagTileManager { get; private set; }
        public GribTileManager GribTileManager { get; private set; }
        public CommandHandler CommandHandler { get; private set; }
        public string Callsign { get; set; }
        public string Cid { get; set; }
        public string Password { get; set; }
        public string FullName { get; set; } = "Simulator Pilot";
        public string Server { get; set; }
        public int Port { get; set; }
        public int Squawk { get; set; } = 0;
        public GeoPoint Position { get; set; } = new GeoPoint(0, 0, 10000);
        public Action<string> LogInfo { get; set; }
        public Action<string> LogWarn { get; set; }
        public Action<string> LogError { get; set; }
        public string AircraftType { get; set; } = "A320";
        public ProtocolRevision Protocol { get; set; } = ProtocolRevision.Classic;
        public Bearing HeadingMag { get; set; } = Bearing.FromDegrees(0);
        public double Speed { get; set; } = 250;
        public bool IsSpeedMach { get; set; } = false;
        public TransponderModeType XpdrMode { get; set; } = TransponderModeType.ModeC;
        public int DelayMs { get; set; } = 0;
        public string EsFlightPlanStr { get; set; } = "";
        public int RequestedAlt { get; set; } = -1;

        internal AircraftBuilder(MagneticTileManager magTileMgr, GribTileManager gribTileMgr, CommandHandler commandHandler) {
            MagTileManager = magTileMgr;
            GribTileManager = gribTileMgr;
            CommandHandler = commandHandler;
            LogInfo = (string msg) => { Console.WriteLine($"{Callsign}: [INFO] {msg}"); };
            LogWarn = (string msg) => { Console.WriteLine($"{Callsign}: [WARN] {msg}"); };
            LogError = (string msg) => { Console.WriteLine($"{Callsign}: [ERROR] {msg}"); };
        }

        public AircraftBuilder(string callsign, string cid, string password, string server, int port, MagneticTileManager magTileMgr, GribTileManager gribTileMgr, CommandHandler commandHandler) : this(magTileMgr, gribTileMgr, commandHandler)
        {
            Callsign = callsign;
            Cid = cid;
            Server = server;
            Port = port;
            Password = password;
        }

        public SimAircraft Create(ClientInfo clientInfo)
        {
            SimAircraft aircraft = new SimAircraft(
                Callsign,
                Cid,
                Password,
                FullName,
                Server,
                (ushort)Port,
                Protocol,
                clientInfo,
                PerfDataHandler.LookupForAircraft(AircraftType),
                Position.Lat,
                Position.Lon,
                Position.Alt,
                HeadingMag,
                MagTileManager,
                GribTileManager,
                CommandHandler,
                DelayMs)
            {
                LogInfo = LogInfo,
                LogWarn = LogWarn,
                LogError = LogError,
                XpdrMode = XpdrMode
            };

            //Figure out if aircraft on ground
            //Find closest airport within 10miles
            //if airplane alt is <= 500ft afe = onground
            var closestAirport = DataHandler.GetAirportByIdentifier(DataHandler.FAKE_AIRPORT_NAME);


            if (closestAirport != null && aircraft.Position.TrueAltitude < closestAirport.Elevation + 500)
            {
                aircraft.Position.OnGround = true;
                aircraft.Position.TrueAltitude = closestAirport.Elevation;
                aircraft.Position.GroundSpeed = Velocity.FromMetersPerSecond(0);
                aircraft.Position.Pitch = Angle.FromRadians(0);
                aircraft.Position.Bank = Angle.FromRadians(0);

                aircraft.FlightPhase = FlightPhaseType.ON_GROUND;
            }

            // Speed
            if (!aircraft.Position.OnGround)
            {
                if (IsSpeedMach)
                {
                    aircraft.Position.MachNumber = Speed;
                }
                else
                {
                    aircraft.Position.IndicatedAirSpeed = Velocity.FromKnots(Speed);
                }
            }

            // Flightplan
            FlightPlan flightPlan;
            try
            {
                flightPlan = FlightPlan.ParseFromEsScenarioFile(EsFlightPlanStr);
                aircraft.FlightPlan = flightPlan;
            }
            catch (FlightPlanException e)
            {
                LogWarn("Error parsing flight plan");
                LogWarn(e.Message);
            }

            // Requested Alt
            if (RequestedAlt >= 0)
            {
                aircraft.Autopilot.SelectedAltitude = RequestedAlt;
                aircraft.Autopilot.CurrentVerticalMode = VerticalModeType.FLCH;
            }

            // Route
            List<Leg> NavDataFormatLegs = new List<Leg>();

            string[] waypoints = aircraft.FlightPlan?.route.Split(' ', '.');

            if (waypoints.Length > 0)
            {
                // Is the first point a SID?

                bool foundSid = false;

                string rawSidName = waypoints[0];

                rawSidName = System.Text.RegularExpressions.Regex.Replace(rawSidName, @"\d", "#");

                for (int j = 9; j > 0; j--)
                {
                    Sid potentialSid = DataHandler.GetSidByAirportAndIdentifier(closestAirport, rawSidName.Replace('#', (char)(j+64)));

                    if (potentialSid != null)
                    {
                        // TODO: Handle runway transition and SID transition. This just loads the 'central' part of the SID
                        foreach (Leg l in potentialSid)
                        {
                            NavDataFormatLegs.Add(l);
                        }

                        // Our last point of the SID should now match with the following FP route point. If not, TF there
                        if (waypoints.Length > 1)
                        {
                            Leg prevLeg = NavDataFormatLegs[NavDataFormatLegs.Count - 1];
                            if (NavDataFormatLegs[NavDataFormatLegs.Count - 1].EndPoint.Identifier != waypoints[1])
                            {
                                Fix nextWp = DataHandler.GetClosestWaypointByIdentifier(waypoints[1], prevLeg.EndPoint.Location);

                                if (nextWp != null)
                                {
                                    Leg tfLeg = new Leg(LegType.TRACK_TO_FIX, null, null, null, null, nextWp, null, null, null, null, null, null, null, null, null);
                                    NavDataFormatLegs.Add(tfLeg);
                                }
                            }
                        }

                        foundSid = true;
                        break;
                    }
                }

                int i = 0;

                if (!foundSid)
                {
                    // DF to first FP waypoint.
                    
                    for (bool foundValidWp = false; !foundValidWp; i++)
                    {
                        Fix firstWp = DataHandler.GetClosestWaypointByIdentifier(waypoints[i], aircraft.Position.PositionGeoPoint);

                        if (firstWp == null)
                        {
                            continue;
                        } else
                        {
                            Leg dfLeg = new Leg(LegType.DIRECT_TO_FIX, null, null, null, null, firstWp, null, null, null, null, null, null, null, null, null);
                            NavDataFormatLegs.Add(dfLeg);
                            foundValidWp = true;
                        }
                    }
                } else
                {
                    i = 2;   
                }

                for (; i < waypoints.Length; i++)
                {
                    // On each iteration of this function we are 'elegible' to start an airway.
                    // So, waypoints[i] could be an airway, a Fix to go direct to, or a DCT meaning we go direct to waypoints[i+1]
                    // DCT is ALWAYS interpreted as direct, then we try to interpret as airway, otherwise as a Fix to go direct to

                    if (waypoints[i] == "DCT")
                    {
                        if (i == waypoints.Length - 1)
                        {
                            // DCT is end of the route. go DCT destination
                            string destAptIdentifier = aircraft.FlightPlan?.destination;
                            
                            Airport dest = DataHandler.GetAirportByIdentifier(destAptIdentifier);

                            if (dest != null )
                            {
                                Leg tfLeg = new Leg(LegType.TRACK_TO_FIX, null, null, null, null, dest, null, null, null, null, null, null, null, null, null);
                                NavDataFormatLegs.Add(tfLeg);
                            }
                        } else
                        {
                            // Just ignore the DCT, we'll create the leg in the next iteration
                            continue;
                        }
                    }

                    if (DataHandler.IsValidAirwayIdentifier(waypoints[i]) && i != waypoints.Length - 1) 
                    {
                        // This is an airway! If we can process it, and select the previous and following waypoint, we'll accept it and add all legs.
                        // Otherwise try to process as waypoint anyways

                        Fix prevWp = NavDataFormatLegs[NavDataFormatLegs.Count - 1].EndPoint;
                        string airwayIdentifier = waypoints[i];
                        
                        // In case there's a duplicate waypoint, use the one closest to the starting waypoint
                        // What if we have a really long airway?
                        // nats moment
                        Fix nextWp = DataHandler.GetClosestWaypointByIdentifier(waypoints[i + 1], prevWp.Location);

                        if (nextWp != null)
                        {
                            try
                            {
                                Airway airway = DataHandler.GetAirwayFromIdentifierAndFixes(airwayIdentifier, prevWp, nextWp);

                                foreach (Leg l in airway)
                                {
                                    NavDataFormatLegs.Add(l);
                                }

                                // All legs added, also, we now have a leg to the next waypoint. So skip that one.
                                i++;
                                continue;
                            } catch (ArgumentException e)
                            {
                                // Airway not valid between those points. We'll try to handle the airway name as a waypoint
                                // So nothing to do here, continue down
                            }
                        }
                    }
                    
                    Fix dctWp = DataHandler.GetClosestWaypointByIdentifier(waypoints[i], NavDataFormatLegs[NavDataFormatLegs.Count - 1].EndPoint.Location);

                    if (dctWp != null)
                    {
                        Leg tfLeg = new Leg(LegType.TRACK_TO_FIX, null, null, null, null, dctWp, null, null, null, null, null, null, null, null, null);
                        NavDataFormatLegs.Add(tfLeg);
                    }
                }
            }


            IList<IRouteLeg> legs = LegFactory.RouteLegsFromNavDataLegs(NavDataFormatLegs, MagTileManager);
            
            /*
            foreach (FactoryFmsWaypoint waypoint in FmsWaypoints)
            {
                Fix nextWp = DataHandler.GetClosestWaypointByIdentifier(waypoint.Identifier, aircraft.Position.PositionGeoPoint);

                if (nextWp != null)
                {
                    FmsPoint fmsPt = new FmsPoint(new RouteWaypoint(nextWp), RoutePointTypeEnum.FLY_BY)
                    {
                        UpperAltitudeConstraint = waypoint.UpperAltitudeConstraint,
                        LowerAltitudeConstraint = waypoint.LowerAltitudeConstraint,
                        SpeedConstraintType = waypoint.SpeedConstratintType,
                        SpeedConstraint = waypoint.SpeedConstraint,

                    };

                    if (lastPoint == null)
                    {
                        lastPoint = fmsPt;
                    }
                    else
                    {
                        legs.Add(new TrackToFixLeg(lastPoint, fmsPt));
                        lastPoint = fmsPt;
                    }

                    if (waypoint.ShouldHold)
                    {
                        PublishedHold pubHold = DataHandler.GetPublishedHold(fmsPt.Point.PointName, fmsPt.Point.PointPosition);

                        if (pubHold != null)
                        {
                            fmsPt.PointType = RoutePointTypeEnum.FLY_OVER;
                            HoldToManualLeg leg = new HoldToManualLeg(lastPoint, BearingTypeEnum.MAGNETIC, pubHold.InboundCourse, pubHold.TurnDirection, pubHold.LegLengthType, pubHold.LegLength, MagTileManager);
                            legs.Add(leg);
                            lastPoint = leg.EndPoint;
                        }
                    }
                }
            }
            */
            foreach (IRouteLeg leg in legs)
            {
                aircraft.Fms.AddRouteLeg(leg);
            }

            if (legs.Count > 0)
            {
                aircraft.Fms.ActivateDirectTo(legs[0].StartPoint.Point);
                aircraft.Autopilot.AddArmedLateralMode(LateralModeType.LNAV);
            }

            return aircraft;
        }

        public class FactoryFmsWaypoint
        {
            public RoutePointTypeEnum PointType { get; set; } = RoutePointTypeEnum.FLY_BY;
            public string Identifier { get; set; }
            public int UpperAltitudeConstraint { get; set; }
            public int LowerAltitudeConstraint { get; set; }
            public ConstraintType SpeedConstratintType { get; set; } = ConstraintType.FREE;
            public double SpeedConstraint { get; set; } = 0;
            public bool ShouldHold { get; set; } = false;

            internal FactoryFmsWaypoint() { }

            public FactoryFmsWaypoint(string identifier)
            {
                Identifier = identifier;
            }
        }
    }
}

