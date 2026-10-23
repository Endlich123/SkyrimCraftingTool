using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Model
{
    [Flags]
    public enum ArmorSlotMask : uint
    {
        None = 0,

        Head = 1 << 0,   // 0x00000001
        Hair = 1 << 1,   // 0x00000002
        Body = 1 << 2,   // 0x00000004
        Hands = 1 << 3,   // 0x00000008
        Forearms = 1 << 4,   // 0x00000010
        Amulet = 1 << 5,   // 0x00000020
        Ring = 1 << 6,   // 0x00000040
        Feet = 1 << 7,   // 0x00000080
        Calves = 1 << 8,   // 0x00000100
        Shield = 1 << 9,   // 0x00000200
        Tail = 1 << 10,  // 0x00000400

        LongHair = 1 << 11,  // 0x00000800
        Circlet = 1 << 12,  // 0x00001000
        Ears = 1 << 13,  // 0x00002000

        // Rare / CK internal slots
        FaceMouth = 1 << 14,  // 0x00004000
        NeckCapeScarf = 1 << 15,  // 0x00008000

        ChestPrimary = 1 << 16,  // 0x00010000
        Back = 1 << 17,  // 0x00020000
        MiscFx1 = 1 << 18,  // 0x00040000
        PelvisPrimary = 1 << 19,  // 0x00080000
        DecapitatedHead = 1 << 20,  // 0x00100000
        DecapitateFx = 1 << 21,  // 0x00200000
        PelvisSecondary = 1 << 22,  // 0x00400000
        LegPrimary = 1 << 23,  // 0x00800000

        // Unused but reserved
        LegSecondary = 1 << 24,
        FaceAltJewelry = 1 << 25,
        ChestSecondary = 1 << 26,
        Shoulders = 1 << 27,
        ArmSecondary = 1 << 28,
        ArmPrimary = 1 << 29,

        // High bits
        MiscFx2 = 1 << 30,
        FX01 = 1u << 31
    }


}
