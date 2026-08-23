using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Models;

namespace ACE.Server.Entity
{
    public class CreateList
    {
        public List<PropertiesCreateList> Items;

        public List<CreateListSet> Sets;    // only for items w/ Treasure flag and probability

        public List<int> ItemSets;  // item index -> set index

        public CreateList(List<PropertiesCreateList> createList)
        {
            Items = createList;

            BuildSets(createList);
        }

        public void BuildSets(List<PropertiesCreateList> createList)
        {
            Sets = new List<CreateListSet>();
            ItemSets = new List<int>();
            var setIdx = -1;

            var totalProbability = 0.0f;
            CreateListSet currentSet = null;

            for (var i = 0; i < createList.Count; i++)
            {
                var item = createList[i];

                var destinationType = (DestinationType)item.DestinationType;
                var useRNG = destinationType.HasFlag(DestinationType.Treasure) && item.Shade != 0;

                var shadeOrProbability = item.Shade;

                if (useRNG)
                {
                    // handle sets in 0-1 chunks
                    if (totalProbability == 0.0f || totalProbability >= 1.0f)
                    {
                        totalProbability = 0.0f;
                        currentSet = new CreateListSet();
                        Sets.Add(currentSet);
                        setIdx++;
                    }

                    var probability = shadeOrProbability;

                    totalProbability += probability;

                    currentSet.Add(item);
                    ItemSets.Add(setIdx);
                }
                else
                    ItemSets.Add(-1);
            }
        }

        /// <summary>
        /// Shadowgain 207: the SET an entry belongs to, so a caller can decide the drop-rate modifier
        /// per set rather than per creature. A Siraluun's list holds its claw AND a key AND two letters,
        /// each in its own 0-1 set - boosting the claw must not boost the rest.
        /// </summary>
        public CreateListSet GetSet(int idx) => Sets[ItemSets[idx]];

        public CreateListSetModifier GetSetModifier(int idx, float modifier)
        {
            var set = Sets[ItemSets[idx]];

            return new CreateListSetModifier(set, modifier);
        }
    }
}
