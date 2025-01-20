﻿using AviationCalcUtilNet.GeoTools;
using NavData_Interface.Objects.Fixes;
using NavData_Interface.Objects.LegCollections.Legs;
using System;
using System.Collections.Generic;
using System.Text;

namespace NavData_Interface.Objects.LegCollections.Airways
{
    public class Airway : LegCollection
    {
        private List<AirwayPoint> _points;

        public List<Leg> SelectedLegs { get; private set; }

        public override IEnumerator<Leg> GetEnumerator()
        {
            return SelectedLegs.GetEnumerator();
        }

        private int _selectedStartPointIndex;

        private int _selectedEndPointIndex;

        public void SelectSection(Fix startFix, Fix endFix)
        {
            _selectedStartPointIndex = -1;
            _selectedEndPointIndex = -1;

            for (int i = 0; i < _points.Count; i++)
            {
                if (_points[i].Point.Identifier == startFix.Identifier)
                {
                    if (_selectedStartPointIndex != -1)
                    {
                        var currentDistance = GeoPoint.FlatDistance(startFix.Location, _points[_selectedStartPointIndex].Point.Location);
                        var newDistance = GeoPoint.FlatDistance(startFix.Location, _points[i].Point.Location);

                        if (newDistance < currentDistance)
                        {
                            _selectedStartPointIndex = i;
                        }

                        continue;
                    }

                    _selectedStartPointIndex = i;
                }
                
                if (_points[i].Point.Identifier == endFix.Identifier)
                {
                    if (_selectedStartPointIndex != -1)
                    {
                        var currentDistance = GeoPoint.FlatDistance(endFix.Location, _points[_selectedEndPointIndex].Point.Location);
                        var newDistance = GeoPoint.FlatDistance(endFix.Location, _points[i].Point.Location);

                        if (newDistance < currentDistance)
                        {
                            _selectedEndPointIndex = i;
                        }

                        continue;
                    }

                    _selectedEndPointIndex = i;
                }
            }

            if (_selectedEndPointIndex == -1 || _selectedStartPointIndex == -1)
            {
                throw new ArgumentException("This airway doesn't link these points");
            }

            List<Leg> legs = new List<Leg>();

            int increment;

            if (_selectedEndPointIndex > _selectedStartPointIndex)
            {
                increment = 1;
            } else
            {
                increment = -1;
            }

            for (var i = _selectedStartPointIndex; i != _selectedEndPointIndex; i += increment)
            {
                var legStartPoint = _points[i];
                var legEndPoint = _points[i + increment];

                if (legStartPoint.Description.IsEndOfRoute)
                {
                    throw new ArgumentException("There is a discontinuity in the airway between these points");
                }

                legs.Add(
                    new Leg(
                        LegType.TRACK_TO_FIX,
                        legStartPoint.Point,
                        legEndPoint.Point,
                        legStartPoint.Description,
                        legEndPoint.Description));
            }

            SelectedLegs = legs;
        }

        internal Airway(List<AirwayPoint> points) : base()
        {
            if (_points.Count < 2)
            {
                throw new ArgumentException("This airway has too few points!");
            }

            _points = points;
        }

        internal Airway(List<AirwayPoint> points, Fix startFix, Fix endFix) : this(points)
        {
            SelectSection(startFix, endFix);
        }
    }
}
