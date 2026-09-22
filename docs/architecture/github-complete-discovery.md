# Complete migration discovery

GS2-09.1 turns a live GitHub inventory into a content-addressed discovery fact. It does not transform,
migrate, archive, or mutate any subject. A qualified result contains two complete reads over the same
source revision. Each read covers the same closed authority population, retains a terminal pagination
proof and high-water mark for every authority, and produces the same normalized digest.

The authority population is:

- `issues-open-and-relevant-closed`
- `project-items`
- `project-fields`
- `hierarchy-and-dependencies`
- `claim-and-event-streams`
- `review-delivery-release-records`
- `repository-settings`
- `workflow-pins`
- `receiver-identities`

Subjects are ordered by stable global identity and bind their observed revision and payload SHA-256.
Observation timestamps remain in each pass but are excluded from the normalized digest. High-water marks,
page counts, item counts, subject identities, revisions, and payload hashes are included. A missing terminal
page, an unknown authority, duplicate or reordered identity, a changed high-water mark, or one subject added
between reads prevents qualification. This keeps absence a proven property of a complete read rather than an
inference from an empty or interrupted response.

The Q5/Q6 contract is pure and read-only. Provider capture is a separately authorized operation whose output
must populate this contract. The later immutable manifest consumes the sealed result; it cannot repair or
reinterpret an incomplete discovery.
