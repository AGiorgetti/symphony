defmodule SymphonyElixir.Shell do
  @moduledoc false

  @spec bash() :: Path.t() | nil
  def bash do
    case :os.type() do
      {:win32, _} ->
        windows_bash()

      _ ->
        System.find_executable("bash") || System.find_executable("sh")
    end
  end

  defp windows_bash do
    git = System.find_executable("git")

    [
      git && Path.expand("../bin/bash.exe", Path.dirname(git)),
      git && Path.expand("../usr/bin/bash.exe", Path.dirname(git)),
      System.find_executable("bash"),
      System.find_executable("sh")
    ]
    |> Enum.reject(&is_nil/1)
    |> Enum.find(&File.exists?/1)
  end
end
