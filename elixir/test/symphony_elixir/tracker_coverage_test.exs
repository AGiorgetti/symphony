defmodule SymphonyElixir.TrackerCoverageTest do
  use SymphonyElixir.TestSupport

  alias SymphonyElixir.Config.Schema
  alias SymphonyElixir.Linear.Issue, as: LinearIssue
  alias SymphonyElixir.Shell
  alias SymphonyElixir.Tracker
  alias SymphonyElixir.Tracker.Issue
  alias SymphonyElixir.Tracker.Memory
  alias SymphonyElixir.Tracker.Registry

  defmodule DelegatingTrackerAdapter do
    @behaviour SymphonyElixir.Tracker

    alias SymphonyElixir.Config.Schema

    def list_active_issues(settings) do
      send(self(), {:delegating_tracker, :list_active_issues, settings.tracker.kind})
      Process.get({__MODULE__, :list_active_issues}, {:ok, []})
    end

    def fetch_issues_by_states(states, settings) do
      send(self(), {:delegating_tracker, :fetch_issues_by_states, states, settings.tracker.kind})
      Process.get({__MODULE__, :fetch_issues_by_states}, {:ok, []})
    end

    def fetch_issue_states_by_ids(issue_ids, settings) do
      send(
        self(),
        {:delegating_tracker, :fetch_issue_states_by_ids, issue_ids, settings.tracker.kind}
      )

      Process.get({__MODULE__, :fetch_issue_states_by_ids}, {:ok, []})
    end

    def claim_issue(issue, settings) do
      send(self(), {:delegating_tracker, :claim_issue, issue.id, settings.tracker.kind})
      Process.get({__MODULE__, :claim_issue}, :ok)
    end

    def post_comment(issue, body, settings) do
      send(self(), {:delegating_tracker, :post_comment, issue.id, body, settings.tracker.kind})
      Process.get({__MODULE__, :post_comment}, {:ok, "delegating-comment"})
    end

    def update_comment(issue, comment_id, body, settings) do
      send(
        self(),
        {:delegating_tracker, :update_comment, issue.id, comment_id, body, settings.tracker.kind}
      )

      Process.get({__MODULE__, :update_comment}, :ok)
    end

    def find_or_create_workpad_comment(issue, marker, settings) do
      send(
        self(),
        {:delegating_tracker, :find_or_create_workpad_comment, issue.id, marker, settings.tracker.kind}
      )

      Process.get({__MODULE__, :find_or_create_workpad_comment}, {:ok, "workpad-comment"})
    end

    def update_issue_state(issue, state_name, settings) do
      send(
        self(),
        {:delegating_tracker, :update_issue_state, issue.id, state_name, settings.tracker.kind}
      )

      Process.get({__MODULE__, :update_issue_state}, :ok)
    end

    def resolve_active_states(%Schema{} = settings) do
      send(self(), {:delegating_tracker, :resolve_active_states, settings.tracker.kind})
      Process.get({__MODULE__, :resolve_active_states}, ["Delegated Active"])
    end

    def resolve_terminal_states(%Schema{} = settings) do
      send(self(), {:delegating_tracker, :resolve_terminal_states, settings.tracker.kind})
      Process.get({__MODULE__, :resolve_terminal_states}, ["Delegated Terminal"])
    end
  end

  test "tracker wrapper delegates reads and writes through the configured adapter" do
    issue = %Issue{id: "issue-1", identifier: "MT-1", state: "Todo"}

    Process.put({DelegatingTrackerAdapter, :list_active_issues}, {:ok, [issue]})
    Process.put({DelegatingTrackerAdapter, :fetch_issues_by_states}, {:ok, [issue]})
    Process.put({DelegatingTrackerAdapter, :fetch_issue_states_by_ids}, {:ok, [issue]})
    Process.put({DelegatingTrackerAdapter, :post_comment}, {:ok, "comment-1"})
    Process.put({DelegatingTrackerAdapter, :find_or_create_workpad_comment}, {:ok, "comment-2"})
    Process.put({DelegatingTrackerAdapter, :resolve_active_states}, ["Queued"])
    Process.put({DelegatingTrackerAdapter, :resolve_terminal_states}, ["Done"])

    write_workflow_file!(Workflow.workflow_file_path(),
      tracker_kind: "memory",
      tracker_module: "#{__MODULE__}.DelegatingTrackerAdapter"
    )

    assert Tracker.adapter() == DelegatingTrackerAdapter
    assert {:ok, [^issue]} = Tracker.fetch_candidate_issues()
    assert {:ok, [^issue]} = Tracker.fetch_issues_by_states(["Todo"])
    assert {:ok, [^issue]} = Tracker.fetch_issue_states_by_ids(["issue-1"])
    assert :ok = Tracker.create_comment("issue-1", "hello")
    assert :ok = Tracker.claim_issue(issue)
    assert {:ok, "comment-1"} = Tracker.post_comment(issue, "hello")
    assert :ok = Tracker.update_comment(issue, "comment-1", "updated")
    assert {:ok, "comment-2"} = Tracker.find_or_create_workpad_comment(issue, "## Codex Workpad")
    assert :ok = Tracker.update_issue_state("issue-1", "Done")
    assert Tracker.resolve_active_states() == ["Queued"]
    assert Tracker.resolve_terminal_states() == ["Done"]

    assert_receive {:delegating_tracker, :list_active_issues, "memory"}
    assert_receive {:delegating_tracker, :fetch_issues_by_states, ["Todo"], "memory"}
    assert_receive {:delegating_tracker, :fetch_issue_states_by_ids, ["issue-1"], "memory"}
    assert_receive {:delegating_tracker, :post_comment, "issue-1", "hello", "memory"}
    assert_receive {:delegating_tracker, :claim_issue, "issue-1", "memory"}
    assert_receive {:delegating_tracker, :post_comment, "issue-1", "hello", "memory"}
    assert_receive {:delegating_tracker, :update_comment, "issue-1", "comment-1", "updated", "memory"}
    assert_receive {:delegating_tracker, :find_or_create_workpad_comment, "issue-1", "## Codex Workpad", "memory"}
    assert_receive {:delegating_tracker, :update_issue_state, "issue-1", "Done", "memory"}
    assert_receive {:delegating_tracker, :resolve_active_states, "memory"}
    assert_receive {:delegating_tracker, :resolve_terminal_states, "memory"}
  end

  test "tracker returns adapter errors and fallback values when registry resolution fails" do
    Process.put({DelegatingTrackerAdapter, :post_comment}, {:error, :comment_failed})

    write_workflow_file!(Workflow.workflow_file_path(),
      tracker_kind: "memory",
      tracker_module: "#{__MODULE__}.DelegatingTrackerAdapter"
    )

    assert {:error, :comment_failed} = Tracker.create_comment("issue-1", "hello")

    write_workflow_file!(Workflow.workflow_file_path(),
      tracker_kind: "unsupported",
      tracker_module: nil,
      tracker_project_slug: nil,
      tracker_repo: nil
    )

    assert {:error, {:unsupported_tracker_kind, "unsupported"}} = Tracker.fetch_candidate_issues()
    assert Tracker.resolve_active_states() == []
    assert Tracker.resolve_terminal_states() == []

    assert_raise ArgumentError, ~r/Unsupported tracker adapter/, fn ->
      Tracker.adapter()
    end
  end

  test "registry resolves configured modules and reports invalid tracker configuration" do
    assert {:ok, Memory} = Registry.resolve_module(%{module: "SymphonyElixir.Tracker.Memory"})
    assert {:error, :missing_tracker_kind} = Registry.resolve_module(%{module: "   ", kind: nil})

    assert {:error, {:invalid_tracker_module, "SymphonyElixir..Broken"}} =
             Registry.resolve_module(%{module: "SymphonyElixir..Broken"})

    assert {:error, :missing_tracker_kind} = Registry.resolve_module(%{})
    assert {:error, {:unsupported_tracker_kind, "jira"}} = Registry.resolve_module(%{kind: "jira"})

    assert {:ok, Memory} =
             Registry.resolve(%Schema{tracker: %Schema.Tracker{module: "SymphonyElixir.Tracker.Memory"}})

    assert {:error, {:tracker_module_unavailable, module, _reason}} =
             Registry.resolve(%Schema{tracker: %Schema.Tracker{module: "SymphonyElixir.DoesNotExist"}})

    assert module == SymphonyElixir.DoesNotExist
  end

  test "memory adapter wrapper helpers filter configured issues and emit events" do
    issue = %Issue{id: "issue-1", identifier: "MT-1", state: " In Progress "}
    other_issue = %Issue{id: "issue-2", identifier: "MT-2", state: nil}
    settings = %Schema{tracker: %Schema.Tracker{active_states: ["Todo"], terminal_states: ["Done"]}}

    Application.put_env(:symphony_elixir, :memory_tracker_issues, [issue, other_issue, %{id: "ignore"}])
    Application.put_env(:symphony_elixir, :memory_tracker_recipient, self())

    assert {:ok, [^issue, ^other_issue]} = Memory.fetch_candidate_issues()
    assert {:ok, [^issue, ^other_issue]} = Memory.fetch_issues_by_states(["in progress", 42])
    assert {:ok, [^other_issue]} = Memory.fetch_issue_states_by_ids(["issue-2"])
    assert :ok = Memory.claim_issue(issue, settings)
    assert :ok = Memory.create_comment("issue-1", "note")
    assert {:ok, "memory-comment:issue-1"} = Memory.post_comment(issue, "note", settings)
    assert :ok = Memory.update_comment(issue, "comment-1", "updated", settings)

    assert {:ok, "memory-workpad:issue-1"} =
             Memory.find_or_create_workpad_comment(issue, "## Codex Workpad", settings)

    assert :ok = Memory.update_issue_state(issue, "Done", settings)
    assert :ok = Memory.update_issue_state("issue-2", "Done")
    assert Memory.resolve_active_states(settings) == ["Todo"]
    assert Memory.resolve_terminal_states(settings) == ["Done"]

    assert_receive {:memory_tracker_claim, "issue-1"}
    assert_receive {:memory_tracker_comment, "issue-1", "note"}
    assert_receive {:memory_tracker_comment, "issue-1", "note"}
    assert_receive {:memory_tracker_comment_update, "issue-1", "comment-1", "updated"}
    assert_receive {:memory_tracker_workpad, "issue-1", "## Codex Workpad", "memory-workpad:issue-1"}
    assert_receive {:memory_tracker_state_update, "issue-1", "Done"}
    assert_receive {:memory_tracker_state_update, "issue-2", "Done"}
  end

  test "shell resolution supports injected unix and windows environments" do
    unix_finder = fn
      "bash" -> "/bin/bash"
      "sh" -> "/bin/sh"
      _ -> nil
    end

    git_path = "/tools/git/cmd/git.exe"
    expected_git_bash = Path.expand("../bin/bash.exe", Path.dirname(git_path))
    expected_usr_bash = Path.expand("../usr/bin/bash.exe", Path.dirname(git_path))

    windows_finder = fn
      "git" -> git_path
      "bash" -> "/fallback/bash.exe"
      "sh" -> "/fallback/sh.exe"
      _ -> nil
    end

    windows_exists = fn
      ^expected_usr_bash -> true
      _path -> false
    end

    assert Shell.bash({:unix, :linux}, unix_finder, fn _path -> false end) == "/bin/bash"
    assert Shell.bash({:win32, :nt}, windows_finder, windows_exists) == expected_usr_bash
    assert expected_git_bash != expected_usr_bash
    assert Shell.bash()
  end

  test "linear issue compatibility helper exposes label names" do
    assert LinearIssue.label_names(%LinearIssue{labels: ["bug", "backend"]}) == ["bug", "backend"]
  end
end
