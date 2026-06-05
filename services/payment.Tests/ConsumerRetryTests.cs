using System.Text;
using FluentAssertions;
using Reservoir.BuildingBlocks.Messaging;

namespace PaymentService.Tests;

/// <summary>
/// Unit tests for the broker-agnostic parts of the shared retry logic
/// (attempt accounting + the retry/dead-letter decision). The publish/ack
/// side-effects in <see cref="ConsumerRetry.HandleFailure"/> need a live
/// broker and are exercised by the live Docker verification instead.
/// </summary>
public class ConsumerRetryTests
{
    [Fact]
    public void ReadRetryCount_is_zero_when_header_absent()
    {
        ConsumerRetry.ReadRetryCount((IDictionary<string, object>?)null).Should().Be(0);
        ConsumerRetry.ReadRetryCount(new Dictionary<string, object>()).Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReadRetryCount_reads_int_header(int value)
    {
        var headers = new Dictionary<string, object> { [ConsumerRetry.RetryCountHeader] = value };
        ConsumerRetry.ReadRetryCount(headers).Should().Be(value);
    }

    [Fact]
    public void ReadRetryCount_coerces_long_and_byte_array_headers()
    {
        // RabbitMQ may surface integer headers as long, and string headers as byte[].
        ConsumerRetry.ReadRetryCount(
            new Dictionary<string, object> { [ConsumerRetry.RetryCountHeader] = 2L }).Should().Be(2);
        ConsumerRetry.ReadRetryCount(
            new Dictionary<string, object> { [ConsumerRetry.RetryCountHeader] = Encoding.UTF8.GetBytes("3") }).Should().Be(3);
    }

    [Fact]
    public void ReadAttemptNumber_is_retry_count_plus_one()
    {
        ConsumerRetry.ReadAttemptNumber((IDictionary<string, object>?)null).Should().Be(1);
        ConsumerRetry.ReadAttemptNumber(
            new Dictionary<string, object> { [ConsumerRetry.RetryCountHeader] = 2 }).Should().Be(3);
    }

    [Theory]
    [InlineData(0, 1)] // first failure  → retry.1 (1s)
    [InlineData(1, 2)] // second failure → retry.2 (2s)
    [InlineData(2, 3)] // third failure  → retry.3 (4s)
    public void Decide_schedules_next_retry_until_max(int retryCount, int expectedNext)
    {
        var decision = ConsumerRetry.Decide(retryCount);

        decision.ShouldRetry.Should().BeTrue();
        decision.NextRetryCount.Should().Be(expectedNext);
        decision.RetryQueueName("payment").Should().Be($"payment.retry.{expectedNext}");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Decide_dead_letters_once_retries_exhausted(int retryCount)
    {
        var decision = ConsumerRetry.Decide(retryCount);

        decision.ShouldRetry.Should().BeFalse();
    }

    [Fact]
    public void Full_lifecycle_is_three_retries_then_dead_letter()
    {
        // Walk the same path a poison message takes: delivery 1..3 each schedule a
        // retry; the 4th delivery (retryCount == MaxRetries) is dead-lettered.
        var queues = new List<string>();
        var retryCount = 0;

        while (true)
        {
            var decision = ConsumerRetry.Decide(retryCount);
            if (!decision.ShouldRetry) break;
            queues.Add(decision.RetryQueueName("payment"));
            retryCount = decision.NextRetryCount;
        }

        queues.Should().Equal("payment.retry.1", "payment.retry.2", "payment.retry.3");
        retryCount.Should().Be(ConsumerRetry.MaxRetries);
    }
}
