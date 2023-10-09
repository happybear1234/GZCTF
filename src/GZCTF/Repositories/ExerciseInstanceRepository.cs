using GZCTF.Extensions;
using GZCTF.Models.Internal;
using GZCTF.Repositories.Interface;
using GZCTF.Services.Cache;
using GZCTF.Services.Interface;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace GZCTF.Repositories;

public class ExerciseInstanceRepository(AppDbContext context,
    IDistributedCache cache,
    IContainerManager service,
    IContainerRepository containerRepository,
    IOptionsSnapshot<ContainerPolicy> containerPolicy,
    ILogger<ExerciseInstanceRepository> logger,
    IStringLocalizer<Program> localizer
) : RepositoryBase(context),
    IExerciseInstanceRepository
{
    public async Task<ExerciseInstance[]> GetExerciseInstances(UserInfo user, CancellationToken token = default)
    {
        if (!await IsExerciseAvailable(token))
            return Array.Empty<ExerciseInstance>();

        ExerciseInstance[] exercises = await context.ExerciseInstances
            .Where(i => i.UserId == user.Id && i.Exercise.IsEnabled)
            .ToArrayAsync(token);

        if (exercises.Length > 0)
            return exercises;

        using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(token);

        var result = new List<ExerciseInstance>();

        await foreach (var id in context.ExerciseChallenges
                           .Where(e => e.IsEnabled && context.ExerciseDependencies.All(d => d.TargetId != e.Id))
                           .Select(e => e.Id).AsAsyncEnumerable())
        {
            var newInst = new ExerciseInstance { ExerciseId = id, UserId = user.Id, IsLoaded = false };

            context.ExerciseInstances.Add(newInst);
            result.Add(newInst);
        }

        await SaveAsync(token);
        await transaction.CommitAsync(token);

        return result.ToArray();
    }

    public async Task<ExerciseInstance?> GetInstance(UserInfo user, int exerciseId, CancellationToken token = default)
    {
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(token);

        ExerciseInstance? instance = await context.ExerciseInstances
            .Include(i => i.FlagContext)
            .Where(e => e.ExerciseId == exerciseId && e.UserId == user.Id)
            .SingleOrDefaultAsync(token);

        // we assume that the user has no permission to access the challenge
        // if the instance does not exist
        if (instance is null)
            return null;

        if (instance.IsLoaded)
        {
            await transaction.CommitAsync(token);
            return instance;
        }

        ExerciseChallenge? exercise = instance.Exercise;

        if (exercise is null || !exercise.IsEnabled)
        {
            await transaction.CommitAsync(token);
            return null;
        }

        try
        {
            // dynamic flag dispatch
            if (instance.Exercise.Type == ChallengeType.DynamicContainer)
                instance.FlagContext = new()
                {
                    Exercise = exercise,
                    // tiny probability will produce the same FLAG,
                    // but this will not affect the correctness of the answer
                    Flag = exercise.GenerateDynamicFlag(),
                    IsOccupied = true
                };

            // instance.FlagContext is null by default
            // static flag does not need to be dispatched

            instance.IsLoaded = true;
            await SaveAsync(token);
            await transaction.CommitAsync(token);
        }
        catch
        {
            logger.SystemLog(localizer[nameof(Resources.Program.InstanceRepository_GetInstanceFailed), user.UserName!, exercise.Title, exercise.Id],
                TaskStatus.Failed, LogLevel.Warning);
            await transaction.RollbackAsync(token);
            return null;
        }

        return instance;
    }

    public async Task<TaskResult<Container>> CreateContainer(ExerciseInstance instance, UserInfo user, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(instance.Exercise.ContainerImage) || instance.Exercise.ContainerExposePort is null)
        {
            logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.InstanceRepository_ContainerCreationFailed), instance.Exercise.Title],
                TaskStatus.Denied, LogLevel.Warning);
            return new TaskResult<Container>(TaskStatus.Failed);
        }

        // containerLimit == 0 means unlimited
        var containerLimit = containerPolicy.Value.MaxExerciseContainerCountPerUser;
        if (containerLimit > 0)
        {
            List<ExerciseInstance> running = await context.ExerciseInstances
                .Where(i => i.User == user && i.Container != null)
                .OrderBy(i => i.Container!.StartedAt).ToListAsync(token);

            ExerciseInstance? first = running.FirstOrDefault();
            if (running.Count >= containerLimit && first is not null)
            {
                logger.Log(
                    Program.StaticLocalizer[nameof(Resources.Program.InstanceRepository_ContainerAutoDestroy), user.UserName!, first.Exercise.Title,
                        first.Container!.ContainerId],
                    user, TaskStatus.Success);
                await containerRepository.DestroyContainer(running.First().Container!, token);
            }
        }

        if (instance.Container is not null)
            return new TaskResult<Container>(TaskStatus.Success, instance.Container);

        await context.Entry(instance).Reference(e => e.FlagContext).LoadAsync(token);

        Container? container = await service.CreateContainerAsync(new ContainerConfig
        {
            TeamId = "exercise",
            UserId = user.Id,
            Flag = instance.FlagContext?.Flag, // static challenge has no specific flag
            Image = instance.Exercise.ContainerImage,
            CPUCount = instance.Exercise.CPUCount ?? 1,
            MemoryLimit = instance.Exercise.MemoryLimit ?? 64,
            StorageLimit = instance.Exercise.StorageLimit ?? 256,
            EnableTrafficCapture = false,
            ExposedPort = instance.Exercise.ContainerExposePort ??
                          throw new ArgumentException(localizer[nameof(Resources.Program.InstanceRepository_InvalidPort)])
        }, token);

        if (container is null)
        {
            logger.SystemLog(Program.StaticLocalizer[nameof(Resources.Program.InstanceRepository_ContainerCreationFailed), instance.Exercise.Title],
                TaskStatus.Failed, LogLevel.Warning);
            return new TaskResult<Container>(TaskStatus.Failed);
        }

        instance.Container = container;
        instance.LastContainerOperation = DateTimeOffset.UtcNow;

        logger.Log(
            Program.StaticLocalizer[nameof(Resources.Program.InstanceRepository_ContainerCreated), user.UserName!, instance.Exercise.Title,
                container.ContainerId], user,
            TaskStatus.Success);

        await SaveAsync(token);

        return new TaskResult<Container>(TaskStatus.Success, instance.Container);
    }

    public async Task<AnswerResult> VerifyAnswer(UserInfo user, ExerciseInstance instance, string answer, CancellationToken token = default)
    {
        if (instance.Exercise.Type == ChallengeType.DynamicContainer)
        {
            if (instance.FlagContext is null)
                return AnswerResult.NotFound;

            if (instance.FlagContext.Flag == answer)
            {
                await MarkSolved(instance, token);
                await UnlockExercises(user, token);
                return AnswerResult.Accepted;
            }

            return AnswerResult.WrongAnswer;
        }

        if (await context.FlagContexts.AsNoTracking()
                .AnyAsync(f => f.ExerciseId == instance.ExerciseId && f.Flag == answer, token))
        {
            await MarkSolved(instance, token);
            await UnlockExercises(user, token);
            return AnswerResult.Accepted;
        }

        return AnswerResult.WrongAnswer;
    }

    Task<bool> IsExerciseAvailable(CancellationToken token = default) =>
        cache.GetOrCreateAsync(logger, CacheKey.ExerciseAvailable, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);
            return context.ExerciseChallenges.AnyAsync(e => e.IsEnabled, token);
        }, token);

    internal async Task MarkSolved(ExerciseInstance instance, CancellationToken token = default)
    {
        if (instance.IsSolved)
            return;

        using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(token);

        instance.IsSolved = true;
        instance.SolveTimeUtc = DateTimeOffset.UtcNow;
        await SaveAsync(token);

        await transaction.CommitAsync(token);
    }

    internal async Task UnlockExercises(UserInfo user, CancellationToken token = default)
    {
        using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(token);

        await foreach (var id in context.ExerciseChallenges.Where(chal => chal.IsEnabled &&
                                                                          context.ExerciseInstances.All(i =>
                                                                              i.UserId == user.Id && i.ExerciseId != chal.Id) &&
                                                                          context.ExerciseDependencies.All(dep => dep.TargetId == chal.Id &&
                                                                              context.ExerciseInstances.Any(
                                                                                  e => e.IsSolved && e.ExerciseId == dep.SourceId
                                                                              ))).Select(e => e.Id).AsAsyncEnumerable())
        {
            var newInst = new ExerciseInstance { ExerciseId = id, UserId = user.Id, IsLoaded = false };

            context.ExerciseInstances.Add(newInst);
        }

        await SaveAsync(token);
        await transaction.CommitAsync(token);
    }
}