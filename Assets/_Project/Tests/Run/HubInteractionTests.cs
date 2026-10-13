using Game.Run;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class HubInteractionTests
    {
        private HubDefinition _definition;
        [SetUp]public void Setup(){_definition=ScriptableObject.CreateInstance<HubDefinition>();_definition.Stations=new[]{new HubDefinition.Station{Kind=HubStationKind.Warehouse,Position=new Vector3(-6,0,0)},new HubDefinition.Station{Kind=HubStationKind.Shop,Position=new Vector3(6,0,0)}};}
        [TearDown]public void Cleanup()=>Object.DestroyImmediate(_definition);
        [Test]public void NearWarehouseSelectsItsActualStation(){Assert.That(_definition.TryFindNearest(new Vector3(-5,0,0),out var p),Is.True);Assert.That(p.Kind,Is.EqualTo(HubStationKind.Warehouse));}
        [Test]public void NearShopSelectsShop(){Assert.That(_definition.TryFindNearest(new Vector3(5,0,0),out var p),Is.True);Assert.That(p.Kind,Is.EqualTo(HubStationKind.Shop));}
        [Test]public void DistantStationHasNoInteraction(){Assert.That(_definition.TryFindNearest(Vector3.zero,out _),Is.False);}
        [Test]public void NearestOverlappingStationWins(){_definition.InteractionRadius=20;Assert.That(_definition.TryFindNearest(new Vector3(4,0,0),out var p),Is.True);Assert.That(p.Kind,Is.EqualTo(HubStationKind.Shop));}
        [Test]public void ExperimentCenterAllowsRealWorldOperations(){Assert.That(_definition.ContainsExperiment(Vector3.zero),Is.True);}
        [Test]public void OutsideExperimentDoesNotAllowWorldOperations(){Assert.That(_definition.ContainsExperiment(new Vector3(4,0,0)),Is.False);}
        [Test]public void InvalidGeometryFailsClosed(){_definition.ExperimentSize=new Vector3(float.NaN,1,1);Assert.That(_definition.ContainsExperiment(Vector3.zero),Is.False);_definition.InteractionRadius=float.NaN;Assert.That(_definition.TryFindNearest(new Vector3(-6,0,0),out _),Is.False);}
        [Test]public void ExperimentUpperBoundaryIsExclusive(){Assert.That(_definition.ContainsExperiment(new Vector3(3,0,0)),Is.False);Assert.That(_definition.ContainsExperiment(new Vector3(-3,0,0)),Is.True);}
    }
}
