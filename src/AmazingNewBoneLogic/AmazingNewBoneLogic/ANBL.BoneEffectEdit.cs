using System;
using MessagePack;
using KKABMX.Core;
using UnityEngine;

namespace AmazingNewBoneLogic
{
    [MessagePackObject]
    public class BoneEffectEdit
    {
        [Key(0)]
        public string Id { get; set; }

        [Key(1)]
        public string Name { get; set; }

        [Key(2)]
        public string BoneName { get; set; }

        [Key(3)]
        public BoneModifierData Modifier { get; set; }

        [Key(4)]
        public int GraphKey { get; set; }

        [Key(5)]
        public bool IsLinked { get; set; }

        [Key(6)]
        public string LinkedBoneName { get; set; }

        [Key(7)]
        public bool LinkScale { get; set; }

        [Key(8)]
        public bool LinkPosition { get; set; }

        [Key(9)]
        public bool LinkRotation { get; set; }

        public BoneEffectEdit()
        {
            Id = Guid.NewGuid().ToString();
            Name = "New Bone Edit";
            BoneName = "";
            Modifier = new BoneModifierData();
        }

        public BoneEffectEdit(string boneName)
        {
            Id = Guid.NewGuid().ToString();
            Name = boneName;
            BoneName = boneName;
            Modifier = new BoneModifierData();
        }

        public BoneEffectEdit Clone()
        {
            return new BoneEffectEdit
            {
                Id = Guid.NewGuid().ToString(), // Give clone a new unique ID
                Name = Name,
                BoneName = BoneName,
                Modifier = Modifier.Clone(),
                GraphKey = GraphKey,
                IsLinked = IsLinked,
                LinkedBoneName = LinkedBoneName,
                LinkScale = LinkScale,
                LinkPosition = LinkPosition,
                LinkRotation = LinkRotation
            };
        }
    }
}
