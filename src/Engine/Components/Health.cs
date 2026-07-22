namespace AsteroidsEngine.Engine.Components;

public struct Health
{
    public int Current;
    public int Max;

    public bool IsDead    => Current <= 0;
    public float Fraction => Max > 0 ? (float)Current / Max : 0f;

    public static Health Full(int max) => new() { Current = max, Max = max };
}
