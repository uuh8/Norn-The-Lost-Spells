using System;
namespace Game.Run
{
    public sealed class ExtractionConditionTracker
    {
        private readonly string _key;
        private bool _objective;
        public ulong Collected { get; private set; }
        public ulong Required { get; }
        public bool Ready => Collected >= Required && (_objective || string.IsNullOrEmpty(_key));
        public ExtractionConditionTracker(ExtractionConditionDefinition definition)
        { Required = definition == null ? 0 : (ulong)Math.Max(0, definition.RequiredCollectedGmu); _key = definition?.RequiredObjectiveKey; }
        public void AddCollected(ulong amount) { Collected = ulong.MaxValue - Collected < amount ? ulong.MaxValue : Collected + amount; }
        public bool Complete(string key) { if (key != _key || string.IsNullOrEmpty(key)) return false; _objective = true; return true; }
    }
}
