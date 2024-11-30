﻿using AviationCalcUtilNet.Atmos.Grib;
using AviationCalcUtilNet.Geo;
using AviationCalcUtilNet.GeoTools;
using AviationCalcUtilNet.Magnetic;
using AviationCalcUtilNet.Units;
using FsdConnectorNet;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NavData_Interface.DataSources;
using NavData_Interface.Objects.Fixes;
using NavData_Interface.Objects.Fixes.Waypoints;
using NavData_Interface.Objects.LegCollections.Legs;
using NavData_Interface.Objects.LegCollections.Procedures;
using SaunaSim.Api.Utilities;
using SaunaSim.Api.WebSockets;
using SaunaSim.Core.Data;
using SaunaSim.Core.Data.Loaders;
using SaunaSim.Core.Simulator.Aircraft;
using SaunaSim.Core.Simulator.Commands;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace sauna_tests
{
    [Explicit]
    public class NavDataTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void TestReader() 
        {
            var filePath = "e_dfd_2301.s3db";
            var connectionString = new SqliteConnectionStringBuilder()
            {
                DataSource = filePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            var connection = new SqliteConnection(connectionString);
            connection.Open();

            SqliteCommand command = new SqliteCommand(){
                Connection = connection,
                CommandText = $"SELECT * FROM tbl_sids WHERE airport_identifier == @airport AND procedure_identifier = @sid"
            };

            command.Parameters.AddWithValue("@airport", "EGKK");
            command.Parameters.AddWithValue("@sid", "LAM6M");

            var reader = command.ExecuteReader();

            int i = 0;

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            while (reader.Read()) { i++; }
            stopwatch.Stop();

            Console.WriteLine(stopwatch.Elapsed / i);
        }

        [Test]
        public void TestReader2()
        {
            var filePath = "e_dfd_2301.s3db";
            var connectionString = new SqliteConnectionStringBuilder()
            {
                DataSource = filePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            var connection = new SqliteConnection(connectionString);
            connection.Open();

            SqliteCommand command = new SqliteCommand("SELECT * FROM tbl_sids", connection);

            var reader = command.ExecuteReader();

            int i = 0;
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            while (reader.Read()) { i++; }

            stopwatch.Stop();

            Console.WriteLine(stopwatch.Elapsed/i);
        }

        [Test]
        public static void TestLoadAllSids()
        {
            var filePath = "e_dfd_2301.s3db";
            var connectionString = new SqliteConnectionStringBuilder()
            {
                DataSource = filePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            var connection = new SqliteConnection(connectionString);
            connection.Open();

            var query = new SqliteCommand("SELECT DISTINCT airport_identifier, procedure_identifier FROM tbl_sids", connection);
            var reader = query.ExecuteReader();
            var navDataInterface = new DFDSource("e_dfd_2301.s3db");

            reader.Read();

            while (reader.HasRows)
            {
                var sid = navDataInterface.GetSidByAirportAndIdentifier(reader["airport_identifier"].ToString(), reader["procedure_identifier"].ToString());

                reader.Read();
            }
        }

        [Test]
        public static void TestLoadAllStars()
        {
            var filePath = "e_dfd_2301.s3db";
            var connectionString = new SqliteConnectionStringBuilder()
            {
                DataSource = filePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            var connection = new SqliteConnection(connectionString);
            connection.Open();

            var query = new SqliteCommand("SELECT DISTINCT airport_identifier, procedure_identifier FROM tbl_stars", connection);
            var reader = query.ExecuteReader();
            var navDataInterface = new DFDSource("e_dfd_2301.s3db");

            reader.Read();

            while (reader.HasRows) 
            {
                var sid = navDataInterface.GetStarByAirportAndIdentifier(reader["airport_identifier"].ToString(), reader["procedure_identifier"].ToString());

                reader.Read();
            }
        }

        [Test]
        public static void TestGetStarFromAirportIdentifier()
        {
            var navDataInterface = new DFDSource("e_dfd_2301.s3db");
            var star = navDataInterface.GetStarByAirportAndIdentifier("EGKK", "FAIL");

            Console.WriteLine(star);

            star = navDataInterface.GetStarByAirportAndIdentifier("KIAD", "CAVLR4");
            star.selectTransition("DORRN");
            star.selectRunwayTransition("01R");

            Console.WriteLine(star);

            foreach (var leg in star)
            {
                Console.WriteLine(leg);
            }
        }

        [Test]
        public static void TestGetSidFromAirportIdentifier()
        {
            var navDataInterface = new DFDSource("e_dfd_2301.s3db");

            var sid = navDataInterface.GetSidByAirportAndIdentifier("EGKK", "LAM6M");

            Console.WriteLine(sid);

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            sid = navDataInterface.GetSidByAirportAndIdentifier("KIAD", "JCOBY4");
            sid.selectRunwayTransition("01R");
            sid.selectTransition("AGARD");

            stopwatch.Stop();

            Console.WriteLine(sid);

            foreach (var leg in sid)
            {
                Console.WriteLine(leg);
            }

            Console.WriteLine($"JCOBY time {stopwatch.Elapsed}");
        }

        [Test]
        public static void TestGetClosestAirportWithinRadius1()
        {
            var navDataInterface = new DFDSource("e_dfd_2311.s3db");
            var westOfLoughNeagh = new GeoPoint(54.686784, -6.544965);
            var closestAirport = navDataInterface.GetClosestAirportWithinRadius(westOfLoughNeagh, Length.FromMeters(30_000));
            Assert.That(closestAirport.Identifier, Is.EqualTo("EGAL"));
        }

        [Test]
        public static void TestGetClosestAirportWithinRadius2()
        {
            var navDataInterface = new DFDSource("e_dfd_2311.s3db");
            var point = new GeoPoint(-17.953955, -179.99);
            var closestAirport = navDataInterface.GetClosestAirportWithinRadius(point, Length.FromMeters(100_000));
            Assert.That(closestAirport.Identifier, Is.EqualTo("NFMO"));
        }

        //[Test]
        //public static void TestGetClosestAirportWithinRadius3()
        //{
        //    var navDataInterface = new DFDSource("e_dfd_2311.s3db");
        //    var point = new GeoPoint(-89.75, -142.284902);
        //    var closestAirport = navDataInterface.GetClosestAirportWithinRadius(point, 100_000);
        //    Assert.That(closestAirport.Identifier, Is.EqualTo("NZSP"));
        //}

        [Test]
        public static void TestGetClosestAirportWithinRadius4()
        {
            var navDataInterface = new DFDSource("e_dfd_2311.s3db");
            var point = new GeoPoint(-27.058760, 83.227773);
            var closestAirport = navDataInterface.GetClosestAirportWithinRadius(point, Length.FromMeters(100_000));
            Assert.That(closestAirport, Is.Null);
        }

        [Test]
        public static void TestCombinedSourcePriorities()
        {
            var navigraphSource = new DFDSource("e_dfd_2301.s3db");
            var custom1 = new InMemorySource("custom1");
            var custom2 = new InMemorySource("custom2");

            var navdataInterface = new CombinedSource("test_sources", navigraphSource, custom1, custom2);

            var badWillo = new Waypoint("WILLO", new GeoPoint(50.985, -0.1912));
            custom1.AddFix(badWillo);

            var weirdPoint1 = new Waypoint("NOEXIST", new GeoPoint(0.001, 0.001));
            custom1.AddFix(weirdPoint1);

            var weirdPoint2 = new Waypoint("NOEXIST", new GeoPoint(0, 0));

            custom2.AddFix(weirdPoint2);

            var willoResults = navdataInterface.GetFixesByIdentifier("WILLO");
            Assert.That(willoResults[0] != badWillo && willoResults[0] != null);

            var weirdPointResults = navdataInterface.GetFixesByIdentifier("NOEXIST");
            Assert.That(weirdPointResults[0] == weirdPoint1 && weirdPointResults.Count == 1);

            navdataInterface.ChangePriority("custom1", 100);
            weirdPointResults = navdataInterface.GetFixesByIdentifier("NOEXIST");
            Assert.That(weirdPointResults[0] == weirdPoint2 && weirdPointResults.Count == 1);
        }

        [Test]
        public static void TestGetAirwayFromIdentifierAndFixes()
        {
            // Arrange
            var navDataInterface = new DFDSource("e_dfd_2301.s3db"); // Adjust the data file path as necessary
            var startFix = new Waypoint("SANBA", new GeoPoint(52.356701, -1.663610)); // Replace with actual start fix identifier
            var endFix = new Waypoint("HON", new GeoPoint(52.356701, -1.663610));     // Replace with actual end fix identifier

            // Act
            var airway = navDataInterface.GetAirwayFromIdentifierAndFixes("N859", startFix, endFix);

            // Print or Assert outputs to validate results
            Console.WriteLine(airway);

            // Optionally measure execution time
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            airway.SelectSection(startFix, endFix);

            stopwatch.Stop();
            Console.WriteLine($"Execution time: {stopwatch.Elapsed}");

            // Iterate through airway sections, if necessary
            foreach (var section in airway) // Assuming airway has sections
            {
                Console.WriteLine(section);
            }

            // Additional assertions can be added here to validate the result
        }

        [Test]
        public static void TestFlightPlanParser()
        {
            DataHandler.LoadNavigraphDataFile("e_dfd_2301.s3db", "aaa");

            var Handler = new SimAircraftHandler(
                Path.Join(AppDomain.CurrentDomain.BaseDirectory, "magnetic", "WMM.COF"),
                Path.Join(Path.GetTempPath(), "sauna-api", "grib-tiles"),
                (s, i) => { }
            );
            var cmdHandler = new CommandHandler(Handler);
            var webSocketHandler = new WebSocketHandler(Handler);

            var pilot = new AircraftBuilder("RYR36CU", "1", "pass", "127.0.0.1", 6809, Handler.MagTileManager, Handler.GribTileManager, cmdHandler)
            {
                Protocol = ProtocolRevision.Classic,
                Position = new GeoPoint(0, 0, 5000),
                HeadingMag = Bearing.FromDegrees(0),
                LogInfo = (string msg) =>
                {
                    
                },
                LogWarn = (string msg) =>
                {
                    
                },
                LogError = (string msg) =>
                {
                    
                },
                XpdrMode = TransponderModeType.ModeC,
                Squawk = 1200
            };

            pilot.EsFlightPlanStr = "$FPEZY938K:*A:I:A320:420:EGGP:::28000:EGAA:00:00:0:0:::WAL2T WAL M146 IPSET P6 BELZU";

            var aircraft = pilot.Create(PrivateInfoLoader.GetClientInfo(_ => { }));

            Console.WriteLine($"Route is WAL2T WAL M146 IPSET P6 BELZU");

            foreach (var l in aircraft.Fms.GetRouteLegs())
            {
                Console.WriteLine(l);
            }
        }
    }
}
