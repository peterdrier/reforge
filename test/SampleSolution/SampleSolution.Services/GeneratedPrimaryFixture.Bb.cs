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
        total += HandwrittenSecondStep(0);
        total += HandwrittenSecondStep(1);
        total += HandwrittenSecondStep(2);
        total += HandwrittenSecondStep(3);
        total += HandwrittenSecondStep(4);
        total += HandwrittenSecondStep(5);
        total += HandwrittenSecondStep(6);
        total += HandwrittenSecondStep(7);
        total += HandwrittenSecondStep(8);
        total += HandwrittenSecondStep(9);
        total += HandwrittenSecondStep(10);
        total += HandwrittenSecondStep(11);
        total += HandwrittenSecondStep(12);
        total += HandwrittenSecondStep(13);
        total += HandwrittenSecondStep(14);
        total += HandwrittenSecondStep(15);
        total += HandwrittenSecondStep(16);
        total += HandwrittenSecondStep(17);
        total += HandwrittenSecondStep(18);
        total += HandwrittenSecondStep(19);
        total += HandwrittenSecondStep(20);
        total += HandwrittenSecondStep(21);
        total += HandwrittenSecondStep(22);
        total += HandwrittenSecondStep(23);
        total += HandwrittenSecondStep(24);
        total += HandwrittenSecondStep(25);
        total += HandwrittenSecondStep(26);
        total += HandwrittenSecondStep(27);
        total += HandwrittenSecondStep(28);
        total += HandwrittenSecondStep(29);
        total += HandwrittenSecondStep(30);
        total += HandwrittenSecondStep(31);
        total += HandwrittenSecondStep(32);
        total += HandwrittenSecondStep(33);
        total += HandwrittenSecondStep(34);
        total += HandwrittenSecondStep(35);
        total += HandwrittenSecondStep(36);
        total += HandwrittenSecondStep(37);
        total += HandwrittenSecondStep(38);
        total += HandwrittenSecondStep(39);
        total += HandwrittenSecondStep(40);
        total += HandwrittenSecondStep(41);
        total += HandwrittenSecondStep(42);
        total += HandwrittenSecondStep(43);
        total += HandwrittenSecondStep(44);
        total += HandwrittenSecondStep(45);
        total += HandwrittenSecondStep(46);
        total += HandwrittenSecondStep(47);
        total += HandwrittenSecondStep(48);
        total += HandwrittenSecondStep(49);
        total += HandwrittenSecondStep(50);
        total += HandwrittenSecondStep(51);
        return total;
    }
}
