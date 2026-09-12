namespace SolastaBot.ChainCollector;

public sealed record PollScheduleEntry(int FromSeconds, int ToSeconds, int EverySeconds);