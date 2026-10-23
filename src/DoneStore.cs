using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ValheimNavigator
{
    // the locations the player has done (looted, cleared), kept per world in a small text file, one location per line.
    internal sealed class DoneStore
    {
        private const string Header = "# ValheimNavigator done list, one location per line: prefab, x, z";
        private readonly string _path;
        private readonly HashSet<string> _keys = new HashSet<string>();

        internal DoneStore(string path)
        {
            _path = path;
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t');
                if (line.StartsWith("#") || parts.Length != 3) continue;
                if (int.TryParse(parts[1], out _) && int.TryParse(parts[2], out _)) _keys.Add(line);
            }
        }

        // the file of a world, characters a file name cannot hold become underscores
        internal static string FileFor(string folder, string world)
        {
            var name = new string((world ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            if (name.Length == 0) name = "world";
            return Path.Combine(folder, name + ".done.txt");
        }

        internal int Count => _keys.Count;

        internal bool Contains(Entry entry) => _keys.Contains(entry.Key);

        internal bool Mark(Entry entry)
        {
            bool added = _keys.Add(entry.Key);
            if (added) Save();
            return added;
        }

        internal bool Unmark(Entry entry)
        {
            bool removed = _keys.Remove(entry.Key);
            if (removed) Save();
            return removed;
        }

        // forgets the done locations among the given entries, returns how many
        internal int Reset(IEnumerable<Entry> entries)
        {
            int removed = entries.Count(e => _keys.Remove(e.Key));
            if (removed > 0) Save();
            return removed;
        }

        private void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, Header + "\n" + string.Join("\n", _keys.OrderBy(k => k, System.StringComparer.Ordinal)) + "\n", new UTF8Encoding(false));
        }
    }
}
