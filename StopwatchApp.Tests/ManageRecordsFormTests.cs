using System.Reflection;
using StopwatchApp.Controls;
using StopwatchApp.Models;

namespace StopwatchApp.Tests;

/// <summary>Tests the UI-free pagination rules used by <see cref="ManageRecordsForm"/>.</summary>
public sealed class ManageRecordsFormTests
{
  [Fact]
  public Task LapExpansion_BatchesLayoutRetainsControlsAndRebindsAcrossPages() =>
    RunOnStaAsync(() =>
    {
      StopwatchRecord[] records = Enumerable
        .Range(1, 30)
        .Select(id => new StopwatchRecord(id, id, id, id, 50))
        .ToArray();
      using ManageRecordsForm form = new(
        records,
        _ => Task.CompletedTask,
        () => Task.CompletedTask,
        recordId =>
          Task.FromResult<IReadOnlyList<Lap>>(
            Enumerable
              .Range(1, 50)
              .Select(id => new Lap((int)recordId * 100 + id, 0, 0, 1))
              .ToArray()
          ),
        System.Drawing.SystemIcons.Application
      )
      {
        Opacity = 0,
      };
      form.Show();
      int layouts = 0;
      foreach (Control control in Descendants(form))
      {
        control.Layout += (_, _) => layouts++;
      }
      Invoke(form, "ToggleLapsAsync", 1L);
      // A generous event budget catches the old per-lap layout cascade (over 1,000 events),
      // without a machine-speed-dependent wall-clock assertion.
      Assert.InRange(layouts, 1, 200);
      IconTextLabel[] labels = Descendants(form).OfType<IconTextLabel>().ToArray();
      Invoke(form, "ToggleLapsAsync", 1L);
      Assert.Equal(labels, Descendants(form).OfType<IconTextLabel>().ToArray());
      Invoke(form, "ToggleLapsAsync", 1L);
      Assert.Equal(labels, Descendants(form).OfType<IconTextLabel>().ToArray());
      Assert.Contains(
        labels,
        label => label.Visible && label.Text.Contains("Lap 101:", StringComparison.Ordinal)
      );
      Invoke(form, "ChangePage", 1);
      Invoke(form, "ToggleLapsAsync", 6L);
      Assert.Contains(
        Descendants(form),
        control => control.Visible && control.Text.Contains("Lap 601:", StringComparison.Ordinal)
      );
      Assert.DoesNotContain(
        Descendants(form),
        control => control.Visible && control.Text.Contains("Lap 101:", StringComparison.Ordinal)
      );
      Invoke(form, "ChangePage", -1);
      Assert.Contains(
        Descendants(form),
        control => control.Visible && control.Text.Contains("Lap 101:", StringComparison.Ordinal)
      );
      form.Close();
    });

  [Fact]
  public Task DeletionRefresh_PreservesScrollWhileButtonsAreDisabledAndRowsReload() =>
    RunOnStaAsync(() =>
    {
      StopwatchRecord[] records = Enumerable
        .Range(1, 20)
        .Select(id => new StopwatchRecord(id, 0, 0, 1, 20))
        .ToArray();
      using ManageRecordsForm form = new(
        records,
        _ => Task.CompletedTask,
        () => Task.CompletedTask,
        _ =>
          Task.FromResult<IReadOnlyList<Lap>>(
            Enumerable.Range(1, 20).Select(id => new Lap(id, 0, 0, 1)).ToArray()
          ),
        System.Drawing.SystemIcons.Application
      )
      {
        Opacity = 0,
      };
      form.Show();
      Invoke(form, "ToggleLapsAsync", 1L);
      Panel host = Descendants(form).OfType<Panel>().Single(panel => panel.AutoScroll);
      Button delete = Descendants(form)
        .OfType<Button>()
        .Single(button => button.Text == "Delete" && button.Tag is long id && id == 4);
      delete.Focus();
      host.AutoScrollPosition = new System.Drawing.Point(0, 300);
      System.Drawing.Point before = host.AutoScrollPosition;
      Assert.True(before.Y < 0);
      Invoke(form, "SetOperationInProgress", true);
      Assert.Equal(before, host.AutoScrollPosition);
      form.UpdateRecords(records.Where(record => record.Id != 4).ToArray());
      Invoke(form, "SetOperationInProgress", false);
      Assert.Equal(before, host.AutoScrollPosition);
      Assert.Contains(Descendants(form), control => control.AccessibleName == "Hide laps");
      form.Close();
    });

  [Fact]
  public Task EmptyDetails_ExpandAndCollapseOnEveryRecord() =>
    RunOnStaAsync(() =>
    {
      using ManageRecordsForm form = new(
        [new StopwatchRecord(1, 0, 0, 1)],
        _ => Task.CompletedTask,
        () => Task.CompletedTask,
        _ => Task.FromResult<IReadOnlyList<Lap>>([]),
        System.Drawing.SystemIcons.Application
      );
      Button toggle = Descendants(form)
        .OfType<Button>()
        .Single(button => button.AccessibleName == "Show laps");
      Invoke(form, "ToggleLapsAsync", 1L);
      Assert.Equal("Hide laps", toggle.AccessibleName);
      Assert.Contains(
        Descendants(form),
        control => control.Text == "No laps recorded for this session."
      );
      Invoke(form, "ToggleLapsAsync", 1L);
      Assert.Equal("Show laps", toggle.AccessibleName);
    });

  [Fact]
  public Task ReloadAfterDeletion_KeepsExpandedDetailsWithoutRefetching() =>
    RunOnStaAsync(() =>
    {
      int loads = 0;
      StopwatchRecord survivor = new(2, 0, 0, 1, 1);
      using ManageRecordsForm form = new(
        [new StopwatchRecord(1, 0, 0, 1), survivor],
        _ => Task.CompletedTask,
        () => Task.CompletedTask,
        _ =>
        {
          loads++;
          return Task.FromResult<IReadOnlyList<Lap>>([new Lap(1, 0, 0, 1)]);
        },
        System.Drawing.SystemIcons.Application
      );
      Invoke(form, "ToggleLapsAsync", 2L);
      form.UpdateRecords([survivor]);
      Assert.Equal(1, loads);
      Assert.Contains(Descendants(form), control => control.AccessibleName == "Hide laps");
      Assert.Contains(
        Descendants(form),
        control => control.Text.Contains("Lap 1:", StringComparison.Ordinal)
      );
      form.UpdateRecords([]);
      Assert.DoesNotContain(Descendants(form).OfType<Button>(), button => button.Tag is long);
    });

  private static void Invoke(ManageRecordsForm form, string methodName, params object[] arguments)
  {
    MethodInfo? method = typeof(ManageRecordsForm).GetMethod(
      methodName,
      BindingFlags.Instance | BindingFlags.NonPublic
    );
    Assert.NotNull(method);
    object? result = method.Invoke(form, arguments);
    if (result is Task task)
    {
      Assert.True(task.IsCompletedSuccessfully);
    }
  }

  private static IEnumerable<Control> Descendants(Control parent)
  {
    foreach (Control child in parent.Controls)
    {
      yield return child;
      foreach (Control descendant in Descendants(child))
      {
        yield return descendant;
      }
    }
  }

  private static Task RunOnStaAsync(Action test)
  {
    TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Thread thread = new(() =>
    {
      try
      {
        test();
        completion.SetResult();
      }
      catch (Exception exception)
      {
        completion.SetException(exception);
      }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task;
  }

  [Theory]
  [InlineData(0, 1)]
  [InlineData(1, 1)]
  [InlineData(5, 1)]
  [InlineData(6, 2)]
  [InlineData(10, 2)]
  [InlineData(11, 3)]
  public void GetPageCount_ReturnsFiveRecordsPerPage(int recordCount, int expectedPageCount)
  {
    Assert.Equal(expectedPageCount, ManageRecordsForm.GetPageCount(recordCount));
  }

  [Fact]
  public void GetPage_PreservesNewestFirstOrderAndLimitsThePageSize()
  {
    StopwatchRecord[] records = Enumerable
      .Range(1, 21)
      .Select(id => new StopwatchRecord(id, id, id, id))
      .Reverse()
      .ToArray();

    IReadOnlyList<StopwatchRecord> secondPage = ManageRecordsForm.GetPage(records, pageIndex: 1);

    Assert.Equal(5, secondPage.Count);
    Assert.Equal(16, secondPage[0].Id);
    Assert.Equal(12, secondPage[^1].Id);
  }

  [Fact]
  public void ClampPageIndex_MovesToTheLastPageAfterDeletion()
  {
    Assert.Equal(0, ManageRecordsForm.ClampPageIndex(pageIndex: 1, recordCount: 5));
    Assert.Equal(1, ManageRecordsForm.ClampPageIndex(pageIndex: 99, recordCount: 6));
  }
}
