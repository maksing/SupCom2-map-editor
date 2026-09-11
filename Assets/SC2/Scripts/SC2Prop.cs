// SC2Prop.cs
// MonoBehaviour data carrier for a MapProp — stores blueprintPath for a
// future replacement of the placeholder cube with a real model (a .bp
// parser is future work).

using UnityEngine;

public class SC2Prop : MonoBehaviour
{
    public string BlueprintPath = "";
    public float Unknown9;
    public uint Flags;
}
