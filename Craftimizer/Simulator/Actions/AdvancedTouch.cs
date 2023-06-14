namespace Craftimizer.Simulator.Actions;

internal class AdvancedTouch : BaseAction
{
    public AdvancedTouch(Simulation simulation) : base(simulation) { }

    public override ActionCategory Category => ActionCategory.Quality;
    public override int Level => 84;
    public override int ActionId => 100411;

    public override int CPCost => Simulation.GetPreviousAction() is StandardTouch && Simulation.GetPreviousAction(2) is BasicTouch ? 18 : 46;
    public override float Efficiency => 1.50f;

    public override void UseSuccess() =>
        Simulation.IncreaseQuality(Efficiency);
}