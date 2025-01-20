﻿using AviationCalcUtilNet.Geo;
using AviationCalcUtilNet.GeoTools;
using NavData_Interface.Objects.Fixes.Waypoints;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SQLite;
using System.Text;

namespace NavData_Interface.DataSources.DFDUtility
{
    internal static class SQLHelper
    {
        internal static GeoPoint locationFromColumns(SQLiteDataReader reader, string latColumn, string lonColumn)
        {
            return new GeoPoint(
                Latitude.FromDegrees(Double.Parse(reader[latColumn].ToString())),
                Longitude.FromDegrees(Double.Parse(reader[lonColumn].ToString()))
                );
        }

        internal static WaypointDescription waypointDescriptionFromDescriptionString(string descriptionString)
        {
            if (descriptionString.Length != 3)
            {
                throw new FormatException("The waypoint description is invalid because the description field isn't the right length");
            }

            bool isEndOfRoute = false;
            bool isFlyOver = false;
            bool isMissedAppStart = false;

            switch (descriptionString[1])
            {
                case 'B':
                    isEndOfRoute = true;
                    isFlyOver = true;
                    break;
                case 'E':
                    isEndOfRoute = true;
                    break;
                case 'Y':
                    isFlyOver = true;
                    break;
                default:
                    break;
            }

            if (descriptionString[2] == 'M')
            {
                isMissedAppStart = true;
            }

            return new WaypointDescription(isEndOfRoute, isFlyOver, isMissedAppStart);
        }
    }
}