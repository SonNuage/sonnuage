namespace SonNuage.UI.Services;

public enum SwipeAction { None, Next, Previous }

/// <summary>
/// Geste sur le bandeau de lecture : un balayage franc vers la gauche passe au titre suivant, vers la droite au
/// précédent. Un geste surtout vertical reste un défilement de page, un petit glissement reste un toucher.
/// </summary>
public static class TrackSwipe
{
    /// <summary>Distance horizontale minimale, en pixels CSS.</summary>
    public const double Threshold = 60;

    public static SwipeAction Classify(double dx, double dy) =>
        Math.Abs(dx) < Threshold || !Sideways(dx, dy) ? SwipeAction.None
        : dx < 0 ? SwipeAction.Next
        : SwipeAction.Previous;

    /// <summary>Décalage du bandeau pendant le geste : il suit le doigt, seulement de côté.</summary>
    public static double Offset(double dx, double dy) => Sideways(dx, dy) ? dx : 0;

    private static bool Sideways(double dx, double dy) => Math.Abs(dx) > 2 * Math.Abs(dy);
}
