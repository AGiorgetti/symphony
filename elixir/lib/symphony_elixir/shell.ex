defmodule SymphonyElixir.Shell do
  @moduledoc false

  @spec bash() :: Path.t() | nil
  def bash do
    bash(:os.type(), &System.find_executable/1, &File.exists?/1)
  end

  @spec bash(tuple(), (String.t() -> Path.t() | nil), (Path.t() -> boolean())) :: Path.t() | nil
  def bash(os_type, find_executable, file_exists)
      when is_function(find_executable, 1) and is_function(file_exists, 1) do
    case os_type do
      {:win32, _} ->
        windows_bash(find_executable, file_exists)

      _ ->
        find_executable.("bash") || find_executable.("sh")
    end
  end

  defp windows_bash(find_executable, file_exists) do
    git = find_executable.("git")

    [
      git && Path.expand("../bin/bash.exe", Path.dirname(git)),
      git && Path.expand("../usr/bin/bash.exe", Path.dirname(git)),
      find_executable.("bash"),
      find_executable.("sh")
    ]
    |> Enum.reject(&is_nil/1)
    |> Enum.find(file_exists)
  end
end
