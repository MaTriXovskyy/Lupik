using System;

namespace Samples;

public static class Greeter
{
    public static string Greet(string name) => $"Hello, {name}!";

    public static void Main() => Console.WriteLine(Greet("Lupik"));
}
