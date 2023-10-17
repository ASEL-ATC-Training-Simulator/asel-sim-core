﻿using FsdConnectorNet;
using FsdConnectorNet.Args;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SaunaSim.Core.Data;
using SaunaSim.Core.Simulator.Aircraft.Control;
using SaunaSim.Core.Simulator.Aircraft.Control.FMS;
using SaunaSim.Core.Simulator.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;


namespace SaunaSim.Core.Simulator.Aircraft
{

    public enum ConstraintType
    {
        FREE = -2,
        LESS = -1,
        EXACT = 0,
        MORE = 1
    }

    public class SimAircraft : IDisposable
    {
        private Thread _posUpdThread;
        private PauseableTimer _delayTimer;
        private bool _paused;
        private FlightPlan? _flightPlan;
        private bool disposedValue;
        private bool _shouldUpdatePosition = false;
        private ClientInfo _clientInfo;

        // Connection Info
        public LoginInfo LoginInfo { get; private set; }

        public string Callsign => LoginInfo.callsign;

        public Connection Connection { get; private set; }

        public ConnectionStatusType ConnectionStatus { get; private set; } = ConnectionStatusType.WAITING;

        // Simulator Data
        public bool Paused
        {
            get => _paused;
            set
            {
                _paused = value;
                if (DelayMs > 0 && _delayTimer != null)
                {
                    if (!_paused)
                    {
                        _delayTimer.Start();
                    }
                    else
                    {
                        _delayTimer.Pause();
                    }
                }
            }
        }

        private int _simRate;
        public int SimRate
        {
            get => _simRate;
            set
            {
                if (value > 80)
                {
                    _simRate = 80;
                }
                else if (value < 1)
                {
                    _simRate = 1;
                }
                else
                {
                    _simRate = value;
                }

                if (DelayMs > 0 && _delayTimer != null)
                {
                    _delayTimer.RatePercent = _simRate * 10;
                }
            }
        }

        // Aircraft Info
        private AircraftPosition _position;
        public AircraftPosition Position => _position;

        public int Config { get; set; }

        public double ThrustLeverPos { get; set; }

        public double SpeedBrakePos { get; set; }

        public double Mass_kg { get; set; }

        public TransponderModeType XpdrMode { get; set; }

        public int Squawk { get; set; }

        public int DelayMs { get; set; }

        public int DelayRemainingMs => _delayTimer?.TimeRemainingMs() ?? DelayMs;

        public AircraftConfig AircraftConfig { get; set; }

        public string AircraftType { get; private set; }

        public string AirlineCode { get; private set; }

        public FlightPlan? FlightPlan
        {
            get => _flightPlan;
            set
            {
                _flightPlan = value;
                if (ConnectionStatus == ConnectionStatusType.CONNECTED)
                {
                    Connection.SendFlightPlan(value);
                }
            }
        }

        // Loggers
        public Action<string> LogInfo { get; set; }

        public Action<string> LogWarn { get; set; }

        public Action<string> LogError { get; set; }
        

        // Assigned values
        public AircraftControl Control { get; private set; }
        public int Assigned_IAS { get; set; } = -1;
        public ConstraintType Assigned_IAS_Type { get; set; } = ConstraintType.FREE;

        public FlightPhaseType FlightPhase { get; set; }

        public double ThrustLeverVel { get; set; }


        public SimAircraft(string callsign, string networkId, string password, string fullname, string hostname, ushort port, ProtocolRevision protocol, ClientInfo clientInfo, double lat, double lon, double alt, double hdg_mag, int delayMs = 0)
        {
            LoginInfo = new LoginInfo(networkId, password, callsign, fullname, PilotRatingType.Student, hostname, protocol, AppSettingsManager.CommandFrequency, port);
            _clientInfo = clientInfo;
            Connection = new Connection();
            Connection.Connected += OnConnectionEstablished;
            Connection.Disconnected += OnConnectionTerminated;
            Connection.FrequencyMessageReceived += OnFrequencyMessageReceived;

            _simRate = 10;
            _paused = true;
            _position = new AircraftPosition
            {
                Latitude = lat,
                Longitude = lon,
                IndicatedAltitude = alt,
                Heading_Mag = hdg_mag
            };
            Control = new AircraftControl(new HeadingHoldInstruction(Convert.ToInt32(hdg_mag)), new AltitudeHoldInstruction(Convert.ToInt32(alt)));
            DelayMs = delayMs;
            AircraftConfig = new AircraftConfig(true, false, false, true, true, false, false, 0, false, false, new AircraftEngine(true, false), new AircraftEngine(true, false));
            AircraftType = "A320";
            AirlineCode = "JBU";
        }

        public void Start()
        {
            // Set initial assignments
            Position.UpdateGribPoint();

            // Connect if no delay, otherwise start timer
            if (DelayMs <= 0)
            {
                OnTimerElapsed(this, null);
            }
            else
            {
                _delayTimer = new PauseableTimer(DelayMs, _simRate * 10);
                _delayTimer.Elapsed += OnTimerElapsed;

                if (!_paused)
                {
                    _delayTimer.Start();
                }
            }
        }

        private void OnFrequencyMessageReceived(object sender, FrequencyMessageEventArgs e)
        {
            if (e.Frequency == AppSettingsManager.CommandFrequency && e.Message.StartsWith($"{Callsign}, "))
            {
                // Split message into args
                List<string> split = e.Message.Replace($"{Callsign}, ", "").Split(' ').ToList();

                // Loop through command list
                while (split.Count > 0)
                {
                    // Get command name
                    string command = split[0].ToLower();
                    split.RemoveAt(0);

                    split = CommandHandler.HandleCommand(command, this, split, (string msg) =>
                    {
                        string returnMsg = msg.Replace($"{Callsign} ", "");
                        Connection.SendFrequencyMessage(e.Frequency, returnMsg);
                    });
                }
            }
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            DelayMs = -1;
            _delayTimer?.Stop();

            // Connect to FSD Server
            Connection.Connect(_clientInfo, LoginInfo, GetFsdPilotPosition(), AircraftConfig, new PlaneInfo(AircraftType, AirlineCode));
            ConnectionStatus = ConnectionStatusType.CONNECTING;
        }

        private void OnConnectionEstablished(object sender, EventArgs e)
        {
            ConnectionStatus = ConnectionStatusType.CONNECTED;
            // Start Position Update Thread
            _shouldUpdatePosition = true;
            _posUpdThread = new Thread(new ThreadStart(AircraftPositionWorker));
            _posUpdThread.Name = $"{Callsign} Position Worker";
            _posUpdThread.Start();

            // Send Flight Plan
            Connection.SendFlightPlan(FlightPlan);
        }

        private void OnConnectionTerminated(object sender, EventArgs e)
        {
            ConnectionStatus = ConnectionStatusType.DISCONNECTED;
            _shouldUpdatePosition = false;
            _delayTimer?.Stop();
        }

        private void AircraftPositionWorker()
        {
            while (_shouldUpdatePosition)
            {
                // Calculate position
                if (!Paused)
                {
                    int slowDownKts = -2;
                    int speedUpKts = 5;

                    // Calculate Speed Change
                    if (Assigned_IAS != -1)
                    {
                        if (Assigned_IAS <= Position.IndicatedAirSpeed)
                        {
                            Position.IndicatedAirSpeed = Math.Max(Assigned_IAS, Position.IndicatedAirSpeed + (slowDownKts * (_simRate / 10.0) * AppSettingsManager.PosCalcRate / 1000.0));
                        }
                        else
                        {
                            Position.IndicatedAirSpeed = Math.Min(Assigned_IAS, Position.IndicatedAirSpeed + (speedUpKts * (_simRate / 10.0) * AppSettingsManager.PosCalcRate / 1000.0));
                        }
                    }

                    Control.UpdatePosition(ref _position, (int) ((_simRate / 10.0) * AppSettingsManager.PosCalcRate));
                    Connection.UpdatePosition(GetFsdPilotPosition());
                }

                Thread.Sleep(AppSettingsManager.PosCalcRate);
            }
        }

        public PilotPosition GetFsdPilotPosition()
        {
            return new PilotPosition(XpdrMode, (ushort)Squawk, Position.Latitude, Position.Longitude, Position.AbsoluteAltitude, Position.AbsoluteAltitude, Position.PressureAltitude, Position.GroundSpeed, Position.Pitch, Position.Bank, Position.Heading_True, false, Position.Velocity_X_MPerS, Position.Velocity_Y_MPerS, Position.Velocity_Z_MPerS, Position.Pitch_Velocity_RadPerS, Position.Heading_Velocity_RadPerS, Position.Bank_Velocity_RadPerS);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Connection.Dispose();
                    _shouldUpdatePosition = false;
                    _posUpdThread?.Join();
                    _delayTimer?.Stop();
                    _delayTimer?.Dispose();
                }

                Connection = null;
                _posUpdThread = null;
                _position = null;
                Control = null;
                _delayTimer = null;
                disposedValue = true;
            }
        }

        ~SimAircraft()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
