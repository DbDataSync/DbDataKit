// @dbdatakit/store: the Store server's screens, mountable in a host (plan 19, Z06).
export { StoreModule } from './StoreModule'
// What the Workspace's model editor imports (plan 33 SC21d): the model's schema for the form and the helpers around it, so the two editors share one definition of a model's shape.
export { MODEL_SCHEMA, modelSchema } from './modelSchema'
export { isModelName, newModelText, summaryOf } from './model'
