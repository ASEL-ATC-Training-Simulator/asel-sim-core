
using SaunaSim.Core;
using SaunaSim.Core.Simulator.Aircraft.Control.FMS;

namespace SaunaSim.Api.ApiObjects.Aircraft
{
    public class FmsWaypointRequest
    {
        public RoutePointTypeEnum PointType { get; set; }
        public string Identifier { get; set; }
        public int UpperAltitudeConstraint { get; set; }
        public int LowerAltitudeConstraint { get; set; }
        public ConstraintType SpeedConstratintType { get; set; } = ConstraintType.FREE;
        public double SpeedConstraint { get; set; } = 0;
    }
}