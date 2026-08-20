namespace SampleSolution.Services;

/// <summary>
/// The handwritten half of a partial type whose <b>generated</b> declaration comes first. Pairs
/// with <c>GeneratedPartialFixture</c>, which has the opposite ordering: between them the two
/// fixtures pin both branches of the primary-file bug, since either ordering was wrong in its own
/// direction.
/// </summary>
public partial class GeneratedPrimaryFixture
{
    private int HandwrittenSecondStep(int i) => i + 1;

    private int HandwrittenSecondWork()
    {
        int total = 0;
        if (total % 2 == 0) total += HandwrittenSecondStep(0);
        if (total % 3 == 0) total += HandwrittenSecondStep(1);
        if (total % 4 == 0) total += HandwrittenSecondStep(2);
        if (total % 5 == 0) total += HandwrittenSecondStep(3);
        if (total % 6 == 0) total += HandwrittenSecondStep(4);
        if (total % 7 == 0) total += HandwrittenSecondStep(5);
        if (total % 8 == 0) total += HandwrittenSecondStep(6);
        if (total % 9 == 0) total += HandwrittenSecondStep(7);
        if (total % 10 == 0) total += HandwrittenSecondStep(8);
        if (total % 11 == 0) total += HandwrittenSecondStep(9);
        if (total % 12 == 0) total += HandwrittenSecondStep(10);
        if (total % 13 == 0) total += HandwrittenSecondStep(11);
        if (total % 14 == 0) total += HandwrittenSecondStep(12);
        if (total % 15 == 0) total += HandwrittenSecondStep(13);
        if (total % 16 == 0) total += HandwrittenSecondStep(14);
        if (total % 17 == 0) total += HandwrittenSecondStep(15);
        if (total % 18 == 0) total += HandwrittenSecondStep(16);
        if (total % 19 == 0) total += HandwrittenSecondStep(17);
        if (total % 20 == 0) total += HandwrittenSecondStep(18);
        if (total % 21 == 0) total += HandwrittenSecondStep(19);
        if (total % 22 == 0) total += HandwrittenSecondStep(20);
        if (total % 23 == 0) total += HandwrittenSecondStep(21);
        if (total % 24 == 0) total += HandwrittenSecondStep(22);
        if (total % 25 == 0) total += HandwrittenSecondStep(23);
        if (total % 26 == 0) total += HandwrittenSecondStep(24);
        if (total % 27 == 0) total += HandwrittenSecondStep(25);
        if (total % 28 == 0) total += HandwrittenSecondStep(26);
        if (total % 29 == 0) total += HandwrittenSecondStep(27);
        if (total % 30 == 0) total += HandwrittenSecondStep(28);
        if (total % 31 == 0) total += HandwrittenSecondStep(29);
        if (total % 32 == 0) total += HandwrittenSecondStep(30);
        if (total % 33 == 0) total += HandwrittenSecondStep(31);
        if (total % 34 == 0) total += HandwrittenSecondStep(32);
        if (total % 35 == 0) total += HandwrittenSecondStep(33);
        if (total % 36 == 0) total += HandwrittenSecondStep(34);
        if (total % 37 == 0) total += HandwrittenSecondStep(35);
        if (total % 38 == 0) total += HandwrittenSecondStep(36);
        if (total % 39 == 0) total += HandwrittenSecondStep(37);
        if (total % 40 == 0) total += HandwrittenSecondStep(38);
        if (total % 41 == 0) total += HandwrittenSecondStep(39);
        if (total % 42 == 0) total += HandwrittenSecondStep(40);
        if (total % 43 == 0) total += HandwrittenSecondStep(41);
        if (total % 44 == 0) total += HandwrittenSecondStep(42);
        if (total % 45 == 0) total += HandwrittenSecondStep(43);
        if (total % 46 == 0) total += HandwrittenSecondStep(44);
        if (total % 47 == 0) total += HandwrittenSecondStep(45);
        if (total % 48 == 0) total += HandwrittenSecondStep(46);
        if (total % 49 == 0) total += HandwrittenSecondStep(47);
        if (total % 50 == 0) total += HandwrittenSecondStep(48);
        if (total % 51 == 0) total += HandwrittenSecondStep(49);
        if (total % 52 == 0) total += HandwrittenSecondStep(50);
        if (total % 53 == 0) total += HandwrittenSecondStep(51);
        return total;
    }

    // The implementing half of a partial method whose definition sits in the generated file. Scoring
    // it requires resolving PartialImplementationPart; without that the body is invisible.
    private partial int PartialWorkDefinedHere()
    {
        int total = 0;
        if (total % 2 == 0) total += HandwrittenSecondStep(0);
        if (total % 3 == 0) total += HandwrittenSecondStep(1);
        if (total % 4 == 0) total += HandwrittenSecondStep(2);
        if (total % 5 == 0) total += HandwrittenSecondStep(3);
        if (total % 6 == 0) total += HandwrittenSecondStep(4);
        if (total % 7 == 0) total += HandwrittenSecondStep(5);
        if (total % 8 == 0) total += HandwrittenSecondStep(6);
        if (total % 9 == 0) total += HandwrittenSecondStep(7);
        if (total % 10 == 0) total += HandwrittenSecondStep(8);
        if (total % 11 == 0) total += HandwrittenSecondStep(9);
        if (total % 12 == 0) total += HandwrittenSecondStep(10);
        if (total % 13 == 0) total += HandwrittenSecondStep(11);
        if (total % 14 == 0) total += HandwrittenSecondStep(12);
        if (total % 15 == 0) total += HandwrittenSecondStep(13);
        if (total % 16 == 0) total += HandwrittenSecondStep(14);
        if (total % 17 == 0) total += HandwrittenSecondStep(15);
        if (total % 18 == 0) total += HandwrittenSecondStep(16);
        if (total % 19 == 0) total += HandwrittenSecondStep(17);
        if (total % 20 == 0) total += HandwrittenSecondStep(18);
        if (total % 21 == 0) total += HandwrittenSecondStep(19);
        if (total % 22 == 0) total += HandwrittenSecondStep(20);
        if (total % 23 == 0) total += HandwrittenSecondStep(21);
        if (total % 24 == 0) total += HandwrittenSecondStep(22);
        if (total % 25 == 0) total += HandwrittenSecondStep(23);
        if (total % 26 == 0) total += HandwrittenSecondStep(24);
        if (total % 27 == 0) total += HandwrittenSecondStep(25);
        if (total % 28 == 0) total += HandwrittenSecondStep(26);
        if (total % 29 == 0) total += HandwrittenSecondStep(27);
        if (total % 30 == 0) total += HandwrittenSecondStep(28);
        if (total % 31 == 0) total += HandwrittenSecondStep(29);
        if (total % 32 == 0) total += HandwrittenSecondStep(30);
        if (total % 33 == 0) total += HandwrittenSecondStep(31);
        if (total % 34 == 0) total += HandwrittenSecondStep(32);
        if (total % 35 == 0) total += HandwrittenSecondStep(33);
        if (total % 36 == 0) total += HandwrittenSecondStep(34);
        if (total % 37 == 0) total += HandwrittenSecondStep(35);
        if (total % 38 == 0) total += HandwrittenSecondStep(36);
        if (total % 39 == 0) total += HandwrittenSecondStep(37);
        if (total % 40 == 0) total += HandwrittenSecondStep(38);
        if (total % 41 == 0) total += HandwrittenSecondStep(39);
        if (total % 42 == 0) total += HandwrittenSecondStep(40);
        if (total % 43 == 0) total += HandwrittenSecondStep(41);
        if (total % 44 == 0) total += HandwrittenSecondStep(42);
        if (total % 45 == 0) total += HandwrittenSecondStep(43);
        if (total % 46 == 0) total += HandwrittenSecondStep(44);
        if (total % 47 == 0) total += HandwrittenSecondStep(45);
        if (total % 48 == 0) total += HandwrittenSecondStep(46);
        if (total % 49 == 0) total += HandwrittenSecondStep(47);
        if (total % 50 == 0) total += HandwrittenSecondStep(48);
        if (total % 51 == 0) total += HandwrittenSecondStep(49);
        if (total % 52 == 0) total += HandwrittenSecondStep(50);
        if (total % 53 == 0) total += HandwrittenSecondStep(51);
        return total;
    }
}
