using System.Collections.Generic;
using System.Linq;
using KKABMX.Core;

#if KK || KKS
using CoordinateType = ChaFileDefine.CoordinateType;
#elif EC
using CoordinateType = KoikatsuCharaFile.ChaFileDefine.CoordinateType;
#endif

namespace AmazingNewBoneLogic
{
    public class ANBLBoneEffect : BoneEffect
    {
        public override IEnumerable<string> GetAffectedBones(BoneController origin)
        {
            var ctrl = origin.GetComponent<AnblCharaController>();
            if (ctrl == null) return Enumerable.Empty<string>();
            return ctrl.GetAllConfiguredBoneNames();
        }

        public override BoneModifierData GetEffect(string bone, BoneController origin, CoordinateType coordinate)
        {
            var ctrl = origin.GetComponent<AnblCharaController>();
            if (ctrl == null) return null;
            return ctrl.GetAggregateModifier(bone, (int)coordinate);
        }
    }
}
