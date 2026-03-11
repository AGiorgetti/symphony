# Harness Engineering Principles

Distilled from OpenAI's article "Harness Engineering" ([openai.com](https://openai.com/index/harness-engineering/)).

## Core idea

Treat the model as a capable but non-persistent worker operating inside a harness. Performance depends less on a single prompt than on the surrounding system: task definition, available tools, feedback quality, and how quickly the model can detect and recover from mistakes.

## Practical principles

### Tightly define the task

- Specify the exact artifact to produce.
- State constraints up front: files, commands, formatting, ownership, and boundaries.
- Replace vague goals like "improve this" with checkable outcomes.
- Make stopping conditions explicit so the agent does not overrun the task.

### Build evaluation loops early

- Add the cheapest useful checks first: syntax, formatting, targeted tests, dry runs.
- Run evaluations during development, not only at the end.
- Prefer short loops that help the agent steer itself.
- Treat failing evals as navigation signals, not just gatekeepers.

### Use tools strategically

- Give the model tools that compress repeated work or eliminate brittle reasoning.
- Prefer deterministic scripts for fragile transformations.
- Expose search, diff, validation, and environment-inspection tools close to the task.
- Remove tool noise when it distracts from the main path.

### Decompose work around reviewable units

- Split large requests into parts that can be owned, checked, and merged independently.
- Keep the decomposition aligned with actual deliverables, not abstract categories.
- Separate critical-path work from parallel sidecar tasks.
- Ensure each subtask has a concrete completion test.

### Compress and externalize context

- Put durable, non-obvious guidance into repo instructions, skills, scripts, and templates.
- Keep the always-loaded context small; move detailed material into references that are read on demand.
- Prefer canonical sources over repeated explanation in every prompt.
- Make conventions discoverable from filenames and directory layout.

### Make the desired path the easiest path

- Put validation commands where the agent will find them.
- Keep instruction files close to the code they govern.
- Use file and script names that reveal intent.
- Reduce the number of judgment calls required for routine work.

## Repository checklist

Use this when hardening a repo for agentic coding:

1. Is there one obvious instruction entry point such as `AGENTS.md`?
2. Are build, test, lint, and focused validation commands discoverable?
3. Are recurring workflows encoded as skills or scripts instead of prose only?
4. Are output expectations concrete enough for review?
5. Can the agent get fast feedback before running slow end-to-end checks?
6. Are task boundaries explicit about files, ownership, and done conditions?
7. Is detailed reference material separated from trigger metadata?
8. Are common failure modes turned into checks, templates, or guardrails?

## Good applications of this skill

- rewriting `AGENTS.md` to reduce ambiguity and enforce better repo habits
- adding smoke tests or narrow validators for common tasks
- creating Codex skills for recurring workflows with hidden context
- restructuring an automation so it emits a reviewable artifact
- turning a fuzzy engineering request into a constrained, evaluable task

## Failure modes to avoid

- writing long guidance that never changes agent behavior
- relying on a single giant prompt instead of repo-local scaffolding
- adding checks that are so slow they never run during iteration
- splitting work into subtasks that cannot be independently reviewed
- duplicating the same guidance across prompts, docs, and templates
