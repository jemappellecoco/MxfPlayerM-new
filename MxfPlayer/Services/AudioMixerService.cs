using System.Collections.Generic;

namespace MxfPlayer.Services
{
    public class AudioMixerService
    {
        private readonly bool[] _enabled = new bool[8];

        public void SetChannelEnabled(int index, bool enabled)
        {
            if (index < 0 || index >= _enabled.Length) return;
            _enabled[index] = enabled;
        }

        public bool IsChannelEnabled(int index)
        {
            if (index < 0 || index >= _enabled.Length) return false;
            return _enabled[index];
        }

        public List<int> GetSelectedIndices()
        {
            var result = new List<int>();

            for (int i = 0; i < _enabled.Length; i++)
            {
                if (_enabled[i])
                    result.Add(i);
            }

            return result;
        }

        public bool HasAnySelected()
        {
            for (int i = 0; i < _enabled.Length; i++)
            {
                if (_enabled[i])
                    return true;
            }

            return false;
        }

        public void Clear()
        {
            for (int i = 0; i < _enabled.Length; i++)
                _enabled[i] = false;
        }
    }
}