﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using SaunaSim.Api.Controllers;
using SaunaSim.Api.WebSockets;
using SaunaSim.Core.Simulator.Aircraft;
using SaunaSim.Core.Simulator.Commands;

namespace SaunaSim.Api.Services
{
	public class SimAircraftService : ISimAircraftService
	{
		private readonly ILogger<DataController> _logger;

		public SimAircraftHandler Handler { get; private set; }

		public CommandHandler CommandHandler { get; private set; }

		public WebSocketHandler WebSocketHandler { get; private set; }

		public Mutex AircraftListLock => Handler.SimAircraftListLock;

		public List<SimAircraft> AircraftList => Handler.SimAircraftList;

		public List<string> CommandsBuffer { get; private set; }

        public Mutex CommandsBufferLock { get; private set; }

        public SimAircraftService(ILogger<DataController> logger)
		{
			_logger = logger;
			Handler = new SimAircraftHandler(
				Path.Join(AppDomain.CurrentDomain.BaseDirectory, "magnetic", "WMM.COF"),
				Path.Join(Path.GetTempPath(), "sauna-api", "grib-tiles"),
				LogFunc
			);
			CommandHandler = new CommandHandler(Handler);
			WebSocketHandler = new WebSocketHandler(Handler);
			CommandsBuffer = new List<string>();
			CommandsBufferLock = new Mutex();
		}

		private void LogFunc(string msg, int priority)
		{
			switch (priority)
			{
				case 0:
					_logger.LogInformation(msg);
					break;
				case 1:
					_logger.LogWarning(msg);
					break;
				case 2:
					_logger.LogError(msg);
					break;
			}
		}
	}
}

