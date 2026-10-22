{{- define "svc.name" -}}{{ default .Chart.Name .Values.nameOverride }}{{- end -}}
{{- define "svc.image" -}}
{{- if .Values.image.digest -}}{{ .Values.image.registry }}/{{ .Values.image.repository }}@{{ .Values.image.digest }}
{{- else -}}{{ .Values.image.registry }}/{{ .Values.image.repository }}:{{ required "image.tag is required" .Values.image.tag }}{{- end -}}
{{- end -}}
{{- define "svc.labels" -}}
app.kubernetes.io/name: {{ include "svc.name" . }}
app.kubernetes.io/part-of: atmnexus
app.kubernetes.io/version: {{ .Values.image.tag | default "digest" | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}
