using StopwatchApp.Controls;
using StopwatchApp.Models;

namespace StopwatchApp.Tests;

/// <summary>Tests the UI-free pagination rules used by <see cref="ManageRecordsForm"/>.</summary>
public sealed class ManageRecordsFormTests
{
  [Theory]
  [InlineData(0, 1)]
  [InlineData(1, 1)]
  [InlineData(10, 1)]
  [InlineData(11, 2)]
  [InlineData(20, 2)]
  [InlineData(21, 3)]
  public void GetPageCount_ReturnsTenRecordsPerPage(int recordCount, int expectedPageCount)
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

    Assert.Equal(10, secondPage.Count);
    Assert.Equal(11, secondPage[0].Id);
    Assert.Equal(2, secondPage[^1].Id);
  }

  [Fact]
  public void ClampPageIndex_MovesToTheLastPageAfterDeletion()
  {
    Assert.Equal(0, ManageRecordsForm.ClampPageIndex(pageIndex: 1, recordCount: 10));
    Assert.Equal(1, ManageRecordsForm.ClampPageIndex(pageIndex: 99, recordCount: 11));
  }
}
