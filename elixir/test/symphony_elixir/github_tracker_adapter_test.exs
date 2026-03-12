defmodule SymphonyElixir.GitHubTrackerAdapterTest do
  use SymphonyElixir.TestSupport

  alias SymphonyElixir.Config.Schema
  alias SymphonyElixir.GitHub.Client
  alias SymphonyElixir.Tracker.GitHub

  defmodule FakeGitHubClient do
    def list_issues(settings, state) do
      send(self(), {:github_list_issues, settings.tracker.repo, state})

      case Process.get({__MODULE__, :issues, state}) do
        {:error, reason} -> {:error, reason}
        issues when is_list(issues) -> {:ok, issues}
        _ -> {:ok, []}
      end
    end

    def get_issue(_settings, issue_id) do
      send(self(), {:github_get_issue, issue_id})

      case Process.get({__MODULE__, :issue, issue_id}) do
        nil -> {:ok, nil}
        {:error, reason} -> {:error, reason}
        issue -> {:ok, issue}
      end
    end

    def list_issue_comments(_settings, issue_id) do
      send(self(), {:github_list_issue_comments, issue_id})
      {:ok, Process.get({__MODULE__, :comments, issue_id}, [])}
    end

    def create_issue_comment(_settings, issue_id, body) do
      send(self(), {:github_create_issue_comment, issue_id, body})
      {:ok, Process.get({__MODULE__, :created_comment}, %{"id" => 101, "body" => body})}
    end

    def update_issue_comment(_settings, comment_id, body) do
      send(self(), {:github_update_issue_comment, comment_id, body})
      {:ok, %{"id" => comment_id, "body" => body}}
    end

    def update_issue(_settings, issue_id, attrs) do
      send(self(), {:github_update_issue, issue_id, attrs})
      {:ok, attrs}
    end
  end

  setup do
    previous_client_module = Application.get_env(:symphony_elixir, :github_client_module)

    Application.put_env(:symphony_elixir, :github_client_module, FakeGitHubClient)

    on_exit(fn ->
      if is_nil(previous_client_module) do
        Application.delete_env(:symphony_elixir, :github_client_module)
      else
        Application.put_env(:symphony_elixir, :github_client_module, previous_client_module)
      end
    end)

    :ok
  end

  test "lists and normalizes active GitHub issues using label-based states" do
    Process.put(
      {FakeGitHubClient, :issues, "open"},
      [
        %{
          "number" => 12,
          "title" => "Implement adapter",
          "body" => "details",
          "state" => "open",
          "html_url" => "https://github.com/octo/demo/issues/12",
          "labels" => [%{"name" => "In Progress"}, %{"name" => "backend"}],
          "assignees" => [%{"login" => "octocat"}],
          "created_at" => "2026-03-12T10:00:00Z",
          "updated_at" => "2026-03-12T11:00:00Z"
        },
        %{
          "number" => 13,
          "title" => "Terminal issue",
          "body" => "done",
          "state" => "closed",
          "html_url" => "https://github.com/octo/demo/issues/13",
          "labels" => [%{"name" => "Done"}],
          "assignees" => [],
          "created_at" => "2026-03-12T10:00:00Z",
          "updated_at" => "2026-03-12T11:00:00Z"
        }
      ]
    )

    settings = github_settings()

    assert {:ok, [%Issue{} = issue]} = GitHub.list_active_issues(settings)
    assert issue.id == "12"
    assert issue.identifier == "octo/demo#12"
    assert issue.state == "In Progress"
    assert issue.assignee_id == "octocat"
    assert issue.labels == ["in progress", "backend"]
    assert_receive {:github_list_issues, "octo/demo", "open"}
  end

  test "ignores pull requests when listing or fetching issues" do
    Process.put(
      {FakeGitHubClient, :issues, "open"},
      [
        %{
          "number" => 70,
          "title" => "Track me",
          "body" => "",
          "state" => "open",
          "labels" => [%{"name" => "Todo"}]
        },
        %{
          "number" => 71,
          "title" => "Actually a PR",
          "body" => "",
          "state" => "open",
          "labels" => [%{"name" => "Todo"}],
          "pull_request" => %{"url" => "https://api.github.com/repos/octo/demo/pulls/71"}
        }
      ]
    )

    Process.put(
      {FakeGitHubClient, :issue, "71"},
      %{
        "number" => 71,
        "title" => "Actually a PR",
        "body" => "",
        "state" => "open",
        "labels" => [%{"name" => "Todo"}],
        "pull_request" => %{"url" => "https://api.github.com/repos/octo/demo/pulls/71"}
      }
    )

    settings = github_settings()

    assert {:ok, issues} = GitHub.list_active_issues(settings)
    assert Enum.map(issues, & &1.id) == ["70"]

    assert {:ok, []} = GitHub.fetch_issue_states_by_ids(["71"], settings)
  end

  test "fetches active and terminal issues by requested states" do
    Process.put(
      {FakeGitHubClient, :issues, "open"},
      [
        %{"number" => 21, "title" => "Todo", "body" => "", "state" => "open", "labels" => [%{"name" => "Todo"}]}
      ]
    )

    Process.put(
      {FakeGitHubClient, :issues, "closed"},
      [
        %{"number" => 22, "title" => "Done", "body" => "", "state" => "closed", "labels" => [%{"name" => "Done"}]}
      ]
    )

    settings = github_settings()

    assert {:ok, issues} = GitHub.fetch_issues_by_states(["Todo", "Done"], settings)
    assert Enum.map(issues, & &1.id) == ["21", "22"]
    assert_receive {:github_list_issues, "octo/demo", "open"}
    assert_receive {:github_list_issues, "octo/demo", "closed"}
  end

  test "updates issue state by replacing workflow labels and opening or closing the issue" do
    Process.put(
      {FakeGitHubClient, :issue, "34"},
      %{
        "number" => 34,
        "title" => "Need review",
        "body" => "",
        "state" => "open",
        "labels" => [%{"name" => "Todo"}, %{"name" => "backend"}]
      }
    )

    settings = github_settings()

    assert :ok = GitHub.update_issue_state(%Issue{id: "34"}, "In Progress", settings)

    assert_receive {:github_update_issue, "34", %{labels: ["backend", "In Progress"], state: "open"}}

    assert :ok = GitHub.update_issue_state(%Issue{id: "34"}, "Done", settings)

    assert_receive {:github_update_issue, "34", %{labels: ["backend", "Done"], state: "closed"}}
  end

  test "finds and updates the persistent workpad comment" do
    Process.put(
      {FakeGitHubClient, :comments, "55"},
      [
        %{"id" => 900, "body" => "## Codex Workpad\n\nhello"},
        %{"id" => 901, "body" => "other"}
      ]
    )

    settings = github_settings()
    issue = %Issue{id: "55", identifier: "octo/demo#55"}

    assert {:ok, 900} = GitHub.find_or_create_workpad_comment(issue, "## Codex Workpad", settings)
    assert :ok = GitHub.update_comment(issue, 900, "updated", settings)

    assert_receive {:github_list_issue_comments, "55"}
    assert_receive {:github_update_issue_comment, 900, "updated"}
  end

  test "creates the workpad comment when it does not exist" do
    Process.put({FakeGitHubClient, :comments, "56"}, [])
    Process.put({FakeGitHubClient, :created_comment}, %{"id" => 777, "body" => "## Codex Workpad"})

    assert {:ok, 777} =
             GitHub.find_or_create_workpad_comment(
               %Issue{id: "56", identifier: "octo/demo#56"},
               "## Codex Workpad",
               github_settings()
             )

    assert_receive {:github_create_issue_comment, "56", "## Codex Workpad"}
  end

  test "github client paginates lists and uses the api token" do
    request_fun = fn opts ->
      page = opts[:params][:page]

      assert Enum.any?(opts[:headers], fn {key, value} ->
               key == "authorization" and value == "Bearer github-token"
             end)

      body =
        if page == 1 do
          for issue_number <- 1..100 do
            %{"number" => issue_number, "state" => "open"}
          end
        else
          [%{"number" => 101, "state" => "open"}]
        end

      {:ok, %{status: 200, body: body}}
    end

    assert {:ok, issues} =
             Client.list_issues(
               github_settings(),
               "open",
               request_fun: request_fun
             )

    assert length(issues) == 101
  end

  defp github_settings do
    %Schema{
      tracker: %Schema.Tracker{
        kind: "github",
        api_token: "github-token",
        repo: "octo/demo",
        active_states: ["Todo", "In Progress"],
        terminal_states: ["Done", "Closed"]
      }
    }
  end
end
