defmodule SymphonyElixir.LiveGitHubAdapterTest do
  use SymphonyElixir.TestSupport

  require Logger

  alias SymphonyElixir.Tracker.GitHub

  @moduletag :live_e2e
  @moduletag timeout: 180_000
  @workpad_marker "## Codex Workpad"
  @live_github_skip_reason (cond do
                              System.get_env("SYMPHONY_RUN_LIVE_E2E") != "1" ->
                                "set SYMPHONY_RUN_LIVE_E2E=1 to enable the real GitHub adapter smoke test"

                              System.get_env("GITHUB_TOKEN") in [nil, ""] ->
                                "real GitHub adapter smoke test requires GITHUB_TOKEN"

                              true ->
                                nil
                            end)

  @tag skip: @live_github_skip_reason
  test "creates and processes a throwaway GitHub issue through the adapter" do
    test_root =
      Path.join(
        System.tmp_dir!(),
        "symphony-live-github-#{System.unique_integer([:positive])}"
      )

    workflow_root = Path.join(test_root, "workflow")
    workflow_file = Path.join(workflow_root, "WORKFLOW.md")
    workspace_root = Path.join(test_root, "workspaces")
    original_workflow_path = Workflow.workflow_file_path()
    repo = live_repo!()
    issue_title = "Symphony live GitHub adapter smoke #{System.unique_integer([:positive])}"
    issue_body = "Created by the Symphony GitHub adapter live smoke test."

    File.mkdir_p!(workflow_root)

    try do
      Workflow.set_workflow_file_path(workflow_file)

      write_workflow_file!(workflow_file,
        tracker_kind: "github",
        tracker_api_token: "$GITHUB_TOKEN",
        tracker_project_slug: nil,
        tracker_repo: repo,
        workspace_root: workspace_root,
        codex_command: "codex app-server",
        observability_enabled: false
      )

      issue_payload = create_issue!(repo, issue_title, issue_body)
      issue_number = issue_payload["number"] |> to_string()

      settings = Config.settings!()

      issue =
        GitHub.list_active_issues(settings)
        |> case do
          {:ok, issues} ->
            Enum.find(issues, &(&1.id == issue_number)) ||
              flunk("expected live GitHub issue #{issue_number} to be visible in active issues")

          {:error, reason} ->
            flunk("expected live GitHub issue list to succeed, got: #{inspect(reason)}")
        end

      assert {:ok, comment_id} = GitHub.find_or_create_workpad_comment(issue, @workpad_marker, settings)
      assert :ok = GitHub.update_comment(issue, comment_id, "#{@workpad_marker}\n\nlive adapter smoke", settings)
      assert :ok = GitHub.update_issue_state(issue, "Done", settings)

      assert {:ok, [updated_issue]} = GitHub.fetch_issue_states_by_ids([issue.id], settings)
      assert updated_issue.id == issue.id
      assert updated_issue.state == "Done"

      assert {:ok, comments} =
               SymphonyElixir.GitHub.Client.list_issue_comments(settings, issue.id)

      assert Enum.any?(comments, fn
               %{"id" => ^comment_id, "body" => body} ->
                 body == "#{@workpad_marker}\n\nlive adapter smoke"

               _ ->
                 false
             end)
    after
      Workflow.set_workflow_file_path(original_workflow_path)
      File.rm_rf(test_root)
    end
  end

  defp live_repo! do
    case System.get_env("SYMPHONY_LIVE_GITHUB_REPO") || git_remote_repo() do
      repo when is_binary(repo) and repo != "" ->
        repo

      other ->
        flunk("expected a GitHub repo from SYMPHONY_LIVE_GITHUB_REPO or git remote, got: #{inspect(other)}")
    end
  end

  defp git_remote_repo do
    case System.cmd("git", ["remote", "get-url", "origin"], stderr_to_stdout: true) do
      {url, 0} ->
        normalize_repo(String.trim(url))

      _ ->
        nil
    end
  end

  defp normalize_repo("https://github.com/" <> repo) do
    repo |> String.trim_trailing(".git") |> normalize_repo_suffix()
  end

  defp normalize_repo("git@github.com:" <> repo) do
    repo |> String.trim_trailing(".git") |> normalize_repo_suffix()
  end

  defp normalize_repo(_url), do: nil

  defp normalize_repo_suffix(repo) do
    case String.split(repo, "/") do
      [owner, name] when owner != "" and name != "" -> "#{owner}/#{name}"
      _ -> nil
    end
  end

  defp create_issue!(repo, title, body) when is_binary(repo) and is_binary(title) and is_binary(body) do
    case Req.post(
           "https://api.github.com/repos/#{repo}/issues",
           headers: github_headers(),
           json: %{
             title: title,
             body: body,
             labels: ["Todo", "symphony-live-e2e"]
           }
         ) do
      {:ok, %Req.Response{status: status, body: %{} = issue}} when status in [200, 201] ->
        issue

      {:ok, %Req.Response{status: status, body: response_body}} ->
        flunk("expected GitHub issue create to succeed, got HTTP #{status}: #{inspect(response_body)}")

      {:error, reason} ->
        flunk("expected GitHub issue create to succeed, got: #{inspect(reason)}")
    end
  end

  defp github_headers do
    token = System.fetch_env!("GITHUB_TOKEN")

    [
      {"accept", "application/vnd.github+json"},
      {"authorization", "Bearer #{token}"},
      {"user-agent", "symphony-elixir-live-test"},
      {"x-github-api-version", "2022-11-28"}
    ]
  end
end
