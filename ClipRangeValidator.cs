using System;
namespace VideoPlayer
{
    internal static class ClipRangeValidator
    {
        public static string? Error(double? start,double? end,double duration)
        {
            if(!start.HasValue)return "Nie ustawiono początku. Użyj przycisku Początek (I).";
            if(!end.HasValue)return "Nie ustawiono końca. Przesuń film dalej i użyj przycisku Koniec (O).";
            if(!double.IsFinite(start.Value)||!double.IsFinite(end.Value)||start.Value<0)
                return "Zakres zawiera nieprawidłowy czas. Ustaw początek i koniec ponownie.";
            if(end.Value<=start.Value)return "Koniec musi znajdować się za początkiem fragmentu.";
            if(!double.IsFinite(duration)||duration<=0||end.Value>duration)
                return "Zakres wykracza poza długość filmu. Ustaw koniec ponownie.";
            return null;
        }
    }
}
